using System.Globalization;
using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// CREATE / ALTER / DROP EVENT SESSION and the server's Extended Events state;
// see docs/claude/extended-events.md. Each statement reads its whole grammar
// first — the errors real raises as the batch compiles — and only outside skip
// mode resolves its names against the engine's catalog and changes anything.
partial class Simulation
{
    private ExtendedEventsServer? extendedEvents;

    /// <summary>
    /// The server's Extended Events state, made on first use with real's
    /// default sessions in it. <paramref name="batch"/> parses the defaults'
    /// predicates the first time any simulation in the process needs them.
    /// </summary>
    internal ExtendedEventsServer ExtendedEventsFor(BatchContext batch)
    {
        if (Volatile.Read(ref this.extendedEvents) is { } existing)
            return existing;
        var created = new ExtendedEventsServer(DefaultEventSessions.Definitions(batch), this.SeedDate);
        return Interlocked.CompareExchange(ref this.extendedEvents, created, null) ?? created;
    }

    /// <summary>The server's Extended Events state if anything has made it, null before.</summary>
    internal ExtendedEventsServer? ExtendedEventsIfMade => Volatile.Read(ref this.extendedEvents);

    /// <summary>Whether the cursor (on <c>EVENT</c>) opens an <c>EVENT SESSION</c> statement.</summary>
    private static bool AtEventSession(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var isSession = context.GetNextOptional() is Name { Value: var word } && word.Equals("SESSION", StringComparison.OrdinalIgnoreCase);
        context.RestoreCheckpoint(checkpoint);
        return isSession;
    }

    /// <summary>
    /// Parses <c>CREATE EVENT SESSION name ON SERVER</c> with its events,
    /// targets and options. Cursor on entry: the <c>EVENT</c> word. <c>ON
    /// DATABASE</c>, Azure SQL Database's form, is Msg 40133 as it runs (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static bool TryParseCreateEventSession(ParserContext context)
    {
        var (name, onDatabase, scope) = ParseEventSessionHead(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Add } || !IsWordAfter(context, "EVENT"))
        {
            throw SimulatedSqlException.SyntaxErrorNear(context.Token switch
            {
                null => scope,
                ReservedKeyword { Keyword: Keyword.Add } => NextToken(context),
                _ => context.Token,
            });
        }
        var statement = new EventSessionStatement(name);
        ParseEventList(context, statement, allowDrop: false);
        if (context.Token is ReservedKeyword { Keyword: Keyword.Add })
        {
            if (!IsWordAfter(context, "TARGET"))
                throw SimulatedSqlException.SyntaxErrorNear(NextToken(context));
            ParseTargetList(context, statement, allowDrop: false);
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            ParseEventSessionOptions(context, statement);
        RequireEventSessionStatementEnd(context);
        if (context.Batch.IsSkipping)
            return true;

        RejectEventSessionDdlInTransaction(context, "CREATE EVENT SESSION");
        if (onDatabase)
            throw SimulatedSqlException.DatabaseEventSessionNotSupported();
        var simulation = context.Simulation;
        if (!simulation.SessionHoldsServerPermission(context.Connection, Permission.CreateAnyEventSession))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        var server = simulation.ExtendedEventsFor(context.Batch);
        var id = server.AllocateSessionId();
        if (server.Find(name) is not null)
            throw SimulatedSqlException.EventSessionAlreadyExists(name);
        var options = new EventSessionOptions();
        ApplyEventSessionOptions(statement, options, running: false);
        var nextId = 1;
        var events = new List<SessionEvent>();
        foreach (var clause in statement.AddEvents)
        {
            var added = BindSessionEvent(clause, ref nextId);
            foreach (var existing in events)
            {
                if (ReferenceEquals(existing.Event, added.Event))
                    throw SimulatedSqlException.XeObjectSpecifiedMultipleTimes("event", added.Event.QualifiedName, 21);
            }
            events.Add(added);
        }
        var targets = new List<SessionTarget>();
        foreach (var clause in statement.AddTargets)
        {
            var added = BindSessionTarget(clause, ref nextId);
            foreach (var existing in targets)
            {
                if (ReferenceEquals(existing.Target, added.Target))
                    throw SimulatedSqlException.XeObjectSpecifiedMultipleTimes("target", added.Target.QualifiedName, 20);
            }
            targets.Add(added);
        }
        var definition = new EventSessionDefinition(id, name, options, [.. events], [.. targets], nextId);
        ValidateEventSession(definition);
        if (!server.TryAdd(definition))
            throw SimulatedSqlException.EventSessionAlreadyExists(name);
        RecordEventSessionDdl(context, "CREATE_EVENT_SESSION", name);
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER EVENT SESSION name ON SERVER</c> followed by one list
    /// of event changes or one of target changes, then optional options — or
    /// by <c>STATE = START | STOP</c> alone. The statement applies as one
    /// unit: a refusal anywhere leaves the session as it was.
    /// </summary>
    private static bool TryParseAlterEventSession(ParserContext context)
    {
        var (name, onDatabase, scope) = ParseEventSessionHead(context);
        var statement = new EventSessionStatement(name);
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop } when IsWordAfter(context, "EVENT"):
                ParseEventList(context, statement, allowDrop: true);
                break;
            case ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop } when IsWordAfter(context, "TARGET"):
                ParseTargetList(context, statement, allowDrop: true);
                break;
            case ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop }:
                throw SimulatedSqlException.SyntaxErrorNear(NextToken(context));
            case Name { Value: var stateWord } when stateWord.Equals("STATE", StringComparison.OrdinalIgnoreCase):
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                statement.State = context.GetNextRequired() switch
                {
                    Name { Value: var value } when value.Equals("START", StringComparison.OrdinalIgnoreCase) => "START",
                    Name { Value: var value } when value.Equals("STOP", StringComparison.OrdinalIgnoreCase) => "STOP",
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextOptional();
                break;
            case ReservedKeyword { Keyword: Keyword.With }:
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context.Token is null ? scope : context.Token);
        }
        if (statement.State is null && context.Token is ReservedKeyword { Keyword: Keyword.With })
            ParseEventSessionOptions(context, statement);
        RequireEventSessionStatementEnd(context);
        if (context.Batch.IsSkipping)
            return true;

        RejectEventSessionDdlInTransaction(context, "ALTER EVENT SESSION");
        if (onDatabase)
            throw SimulatedSqlException.DatabaseEventSessionNotSupported();
        var simulation = context.Simulation;
        var server = simulation.ExtendedEventsFor(context.Batch);
        var definition = server.Find(name) ?? throw SimulatedSqlException.CannotAlterOrDropEventSession("alter", name, 28);
        var running = server.IsRunning(definition.Id);

        if (statement.State is { } state)
        {
            // The session's state is checked ahead of the permission, so a
            // session without either answers Msg 25704 to a stop of a stopped
            // session (probed 2026-10-16 against SQL Server 2025).
            if (state == "START" && running)
                throw SimulatedSqlException.EventSessionAlreadyStarted();
            if (state == "STOP" && !running)
                throw SimulatedSqlException.EventSessionAlreadyStopped();
            RequireEventSessionPermission(context, state == "START" ? Permission.AlterAnyEventSessionEnable : Permission.AlterAnyEventSessionDisable, name, state == "START" ? (byte)20 : (byte)19);
            if (state == "START")
                server.Start(definition, context.Batch.CurrentStatement.UtcNow);
            else
                server.Stop(definition);
            RecordEventSessionDdl(context, "ALTER_EVENT_SESSION", name);
            return true;
        }

        if (statement.AddEvents.Count != 0)
            RequireEventSessionPermission(context, Permission.AlterAnyEventSessionAddEvent, name, 22);
        if (statement.DropEvents.Count != 0)
            RequireEventSessionPermission(context, Permission.AlterAnyEventSessionDropEvent, name, 23);
        if (statement.AddTargets.Count != 0)
            RequireEventSessionPermission(context, Permission.AlterAnyEventSessionAddTarget, name, 24);
        if (statement.DropTargets.Count != 0)
            RequireEventSessionPermission(context, Permission.AlterAnyEventSessionDropTarget, name, 25);
        if (statement.Options.Count != 0)
            RequireEventSessionPermission(context, Permission.AlterAnyEventSessionOption, name, 21);

        var options = definition.Options.Clone();
        ApplyEventSessionOptions(statement, options, running);
        var nextId = definition.NextObjectId;
        var events = definition.Events.ToList();
        foreach (var drop in statement.DropEvents)
            _ = events.Remove(FindDroppedEvent(events, drop, running));
        foreach (var clause in statement.AddEvents)
        {
            var added = BindSessionEvent(clause, ref nextId);
            foreach (var existing in events)
            {
                if (ReferenceEquals(existing.Event, added.Event))
                {
                    throw definition.Events.Contains(existing)
                        ? SimulatedSqlException.XeObjectAlreadyInSession("event", added.Event.QualifiedName, 21)
                        : SimulatedSqlException.XeObjectSpecifiedMultipleTimes("event", added.Event.QualifiedName, 21);
                }
            }
            events.Add(added);
        }
        var targets = definition.Targets.ToList();
        foreach (var drop in statement.DropTargets)
            _ = targets.Remove(FindDroppedTarget(targets, drop, running));
        foreach (var clause in statement.AddTargets)
        {
            var added = BindSessionTarget(clause, ref nextId);
            foreach (var existing in targets)
            {
                if (ReferenceEquals(existing.Target, added.Target))
                {
                    throw definition.Targets.Contains(existing)
                        ? SimulatedSqlException.XeObjectAlreadyInSession("target", added.Target.QualifiedName, 20)
                        : SimulatedSqlException.XeObjectSpecifiedMultipleTimes("target", added.Target.QualifiedName, 20);
                }
            }
            targets.Add(added);
        }
        var altered = definition.With(options, [.. events], [.. targets], nextId);
        ValidateEventSession(altered, definition.Events);
        server.Replace(altered);
        RecordEventSessionDdl(context, "ALTER_EVENT_SESSION", name);
        return true;
    }

    /// <summary>
    /// Parses <c>DROP EVENT SESSION name ON SERVER</c>, which has no <c>IF
    /// EXISTS</c> and stops a running session as it drops it. A missing
    /// session is Msg 15151 state 26, a session the caller may not drop state
    /// 27 (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static bool TryParseDropEventSession(ParserContext context)
    {
        var (name, onDatabase, _) = ParseEventSessionHead(context);
        RequireEventSessionStatementEnd(context);
        if (context.Batch.IsSkipping)
            return true;

        RejectEventSessionDdlInTransaction(context, "DROP EVENT SESSION");
        if (onDatabase)
            throw SimulatedSqlException.DatabaseEventSessionNotSupported();
        var simulation = context.Simulation;
        var server = simulation.ExtendedEventsFor(context.Batch);
        var definition = server.Find(name) ?? throw SimulatedSqlException.CannotAlterOrDropEventSession("drop", name, 26);
        if (!simulation.SessionHoldsServerPermission(context.Connection, Permission.DropAnyEventSession))
            throw SimulatedSqlException.CannotAlterOrDropEventSession("drop", name, 27);
        server.Remove(definition);
        RecordEventSessionDdl(context, "DROP_EVENT_SESSION", name);
        return true;
    }

    /// <summary>
    /// <c>EVENT SESSION name ON { SERVER | DATABASE }</c>, the cursor on
    /// <c>EVENT</c> on entry and past the scope word on return. A name
    /// starting <c>#</c> is Msg 25701 as the batch compiles.
    /// </summary>
    private static (string Name, bool OnDatabase, Token Scope) ParseEventSessionHead(ParserContext context)
    {
        context.MoveNextRequired();
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        if (name.StartsWith('#'))
            throw SimulatedSqlException.TemporaryEventSessionName(name);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var scope = context.GetNextRequired();
        var onDatabase = scope switch
        {
            Name { Value: var word } when word.Equals("SERVER", StringComparison.OrdinalIgnoreCase) => false,
            ReservedKeyword { Keyword: Keyword.Database } => true,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextOptional();
        return (name, onDatabase, scope);
    }

    private static bool IsWordAfter(ParserContext context, string word) =>
        NextToken(context) is Name { Value: var next } && next.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>The token after the cursor, the cursor left where it was.</summary>
    private static Token? NextToken(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return next;
    }

    /// <summary>
    /// A comma-separated list of <c>ADD EVENT</c> clauses — or, in an
    /// <c>ALTER</c>, of <c>DROP EVENT</c> clauses: real's grammar takes one
    /// verb per list, so a list mixing them is Msg 156 near the second verb.
    /// </summary>
    private static void ParseEventList(ParserContext context, EventSessionStatement statement, bool allowDrop)
    {
        var verb = ((ReservedKeyword)context.Token!).Keyword;
        while (true)
        {
            if (context.Token is not ReservedKeyword { Keyword: var keyword } || keyword != verb)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            context.MoveNextRequired();
            var eventName = XePredicateSyntax.ParseDottedName(context);
            if (verb == Keyword.Drop)
            {
                statement.DropEvents.Add(eventName);
            }
            else
            {
                var clause = new EventClause(eventName);
                if (context.Token is Operator { Character: '(' })
                    ParseEventBody(context, clause);
                statement.AddEvents.Add(clause);
            }
            if (context.Token is not Operator { Character: ',' })
                return;
            context.MoveNextRequired();
            if (!allowDrop && context.Token is ReservedKeyword { Keyword: Keyword.Add } && !IsWordAfter(context, "EVENT"))
                throw SimulatedSqlException.SyntaxErrorNear(NextToken(context));
        }
    }

    /// <summary>
    /// <c>( [SET name = value, …] [ACTION (name, …)] [WHERE predicate] )</c>
    /// after an event, each part at most once and in that order.
    /// </summary>
    private static void ParseEventBody(ParserContext context, EventClause clause)
    {
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Set })
            clause.Sets = ParseAttributeList(context);
        if (context.Token is Name { Value: var actionWord } && actionWord.Equals("ACTION", StringComparison.OrdinalIgnoreCase))
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            while (true)
            {
                clause.Actions.Add(XePredicateSyntax.ParseDottedName(context));
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
            context.MoveNextRequired();
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            clause.Where = XePredicateSyntax.Parse(context);
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
    }

    /// <summary><c>SET name = value [, …]</c>, the cursor on <c>SET</c> on entry and past the last value on return; a bracketed name is Msg 102.</summary>
    private static List<(string Name, XeConstant Value)> ParseAttributeList(ParserContext context)
    {
        var attributes = new List<(string, XeConstant)>();
        while (true)
        {
            if (context.GetNextRequired() is not UnquotedString attribute)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            attributes.Add((attribute.Value, XeConstant.Parse(context)));
            if (context.Token is not Operator { Character: ',' })
                return attributes;
        }
    }

    /// <summary>A comma-separated list of <c>ADD TARGET</c> clauses, or of <c>DROP TARGET</c> clauses in an <c>ALTER</c>.</summary>
    private static void ParseTargetList(ParserContext context, EventSessionStatement statement, bool allowDrop)
    {
        _ = allowDrop;
        var verb = ((ReservedKeyword)context.Token!).Keyword;
        while (true)
        {
            if (context.Token is not ReservedKeyword { Keyword: var keyword } || keyword != verb)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            context.MoveNextRequired();
            var targetName = XePredicateSyntax.ParseDottedName(context);
            if (verb == Keyword.Drop)
            {
                statement.DropTargets.Add(targetName);
            }
            else
            {
                var clause = new TargetClause(targetName);
                if (context.Token is Operator { Character: '(' })
                {
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    clause.Sets = ParseAttributeList(context);
                    if (context.Token is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                }
                statement.AddTargets.Add(clause);
            }
            if (context.Token is not Operator { Character: ',' })
                return;
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// <c>WITH ( option = value [, …] )</c>. An unknown option, or an
    /// <c>ON</c> / <c>OFF</c> option given anything else, is Msg 102 state 30
    /// near the option; an unknown unit Msg 102 state 31 near it; a repeated
    /// option Msg 25702 and a value the grammar can't read Msg 25703 — all as
    /// the batch compiles (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static void ParseEventSessionOptions(ParserContext context, EventSessionStatement statement)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        while (true)
        {
            if (context.Token is not Name optionToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var option = optionToken.Value.ToUpperInvariant();
            if (option is not ("EVENT_RETENTION_MODE" or "MAX_DISPATCH_LATENCY" or "MAX_DURATION" or "MAX_EVENT_SIZE" or "MAX_MEMORY" or "MEMORY_PARTITION_MODE" or "STARTUP_STATE" or "TRACK_CAUSALITY"))
                throw SimulatedSqlException.SyntaxErrorNear(optionToken, 30);
            var lower = OptionDisplayName(option);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var valueToken = context.GetNextRequired();
            long value;
            switch (option)
            {
                case "EVENT_RETENTION_MODE":
                    value = (valueToken as Name)?.Value.ToUpperInvariant() switch
                    {
                        "ALLOW_MULTIPLE_EVENT_LOSS" => 'M',
                        "ALLOW_SINGLE_EVENT_LOSS" => 'S',
                        "NO_EVENT_LOSS" => 'N',
                        _ => throw SimulatedSqlException.EventSessionOptionInvalid(lower, 2),
                    };
                    context.MoveNextRequired();
                    break;
                case "MEMORY_PARTITION_MODE":
                    value = (valueToken as Name)?.Value.ToUpperInvariant() switch
                    {
                        "NONE" => 'G',
                        "PER_CPU" => 'C',
                        "PER_NODE" => 'N',
                        _ => throw SimulatedSqlException.EventSessionOptionInvalid(lower, 2),
                    };
                    context.MoveNextRequired();
                    break;
                case "STARTUP_STATE" or "TRACK_CAUSALITY":
                    value = valueToken switch
                    {
                        ReservedKeyword { Keyword: Keyword.On } => 1,
                        ReservedKeyword { Keyword: Keyword.Off } => 0,
                        _ => throw SimulatedSqlException.SyntaxErrorNear(optionToken, 30),
                    };
                    context.MoveNextRequired();
                    break;
                case "MAX_DISPATCH_LATENCY" when valueToken is Name { Value: var infinite } && infinite.Equals("INFINITE", StringComparison.OrdinalIgnoreCase):
                    value = 0;
                    context.MoveNextRequired();
                    break;
                case "MAX_DURATION" when valueToken is Name { Value: var unlimited } && unlimited.Equals("UNLIMITED", StringComparison.OrdinalIgnoreCase):
                    value = 0;
                    context.MoveNextRequired();
                    break;
                default:
                    if (valueToken is not Numeric { Value.Type: Int32SqlType } number)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    long amount = number.Value.AsInt32;
                    var unitToken = context.GetNextRequired();
                    var unit = (unitToken as Name)?.Value.ToUpperInvariant();
                    if (unitToken is not Name)
                    {
                        throw SimulatedSqlException.EventSessionOptionInvalid(lower, option switch
                        {
                            "MAX_DISPATCH_LATENCY" => 4,
                            _ => 7,
                        });
                    }
                    value = option switch
                    {
                        "MAX_DISPATCH_LATENCY" when unit == "SECONDS" => amount,
                        "MAX_DURATION" => unit switch
                        {
                            "DAYS" => amount * 86_400,
                            "HOURS" => amount * 3_600,
                            "MINUTES" => amount * 60,
                            "SECONDS" => amount,
                            _ => throw SimulatedSqlException.SyntaxErrorNear(unitToken, 31),
                        },
                        "MAX_EVENT_SIZE" or "MAX_MEMORY" => unit switch
                        {
                            "KB" => amount,
                            "MB" => amount * 1024,
                            _ => throw SimulatedSqlException.SyntaxErrorNear(unitToken, 31),
                        },
                        _ => throw SimulatedSqlException.SyntaxErrorNear(unitToken, 31),
                    };
                    // A zero duration is refused as the statement runs, unlike
                    // the shapes above; -1 carries it there.
                    if (option == "MAX_DURATION" && value == 0)
                        value = -1;
                    context.MoveNextRequired();
                    break;
            }
            statement.Options.Add((lower, value));
            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        context.MoveNextOptional();
    }

    /// <summary>An option as real's messages name it, in lower case.</summary>
    private static string OptionDisplayName(string option) => option switch
    {
        "EVENT_RETENTION_MODE" => "event_retention_mode",
        "MAX_DISPATCH_LATENCY" => "max_dispatch_latency",
        "MAX_DURATION" => "max_duration",
        "MAX_EVENT_SIZE" => "max_event_size",
        "MAX_MEMORY" => "max_memory",
        "MEMORY_PARTITION_MODE" => "memory_partition_mode",
        "STARTUP_STATE" => "startup_state",
        _ => "track_causality",
    };

    private static void RequireEventSessionStatementEnd(ParserContext context)
    {
        if (IsStatementBoundary(context.Token))
            return;
        // A second ADD list without its comma reads as a stray word after ADD.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Add } && NextToken(context) is Name next)
            throw SimulatedSqlException.SyntaxErrorNear(next);
        throw context.Token is ReservedKeyword keyword
            ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
            : SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>Event session DDL inside a user transaction is Msg 574, naming the statement.</summary>
    private static void RejectEventSessionDdlInTransaction(ParserContext context, string statement)
    {
        if (context.Connection.CurrentTransaction is not null)
            throw SimulatedSqlException.StatementInsideUserTransaction(statement);
    }

    private static void RequireEventSessionPermission(ParserContext context, Permission permission, string name, byte state)
    {
        if (!context.Simulation.SessionHoldsServerPermission(context.Connection, permission))
            throw SimulatedSqlException.CannotAlterOrDropEventSession("alter", name, state);
    }

    private static void RecordEventSessionDdl(ParserContext context, string eventType, string name) =>
        RecordServerDdlEvent(context, eventType, databaseName: null, loginName: null, objectName: name, objectType: "EVENT SESSION");

    /// <summary>
    /// Settles the options a statement set onto <paramref name="options"/>,
    /// one at a time as written: a repeat is Msg 25702, a dispatch latency
    /// past 2,147,483 seconds or a size of 2 GB or more Msg 25708, a zero
    /// duration Msg 25703, and on a running session any option but
    /// <c>STARTUP_STATE</c> Msg 25707; then a memory size its buffers can't
    /// hold is Msg 25632, and an event size smaller than one buffer Msg 25640
    /// — all as the statement runs, ahead of its events and targets (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static void ApplyEventSessionOptions(EventSessionStatement statement, EventSessionOptions options, bool running)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (option, value) in statement.Options)
        {
            if (!seen.Add(option))
                throw SimulatedSqlException.EventSessionOptionRepeated(option);
            if (running && option != "startup_state")
                throw SimulatedSqlException.EventSessionOptionWhileRunning(option);
            switch (option)
            {
                case "event_retention_mode":
                    options.RetentionMode = (char)value;
                    break;
                case "max_dispatch_latency":
                    if (value > 2_147_483)
                        throw SimulatedSqlException.EventSessionOptionTooLarge(option, 1);
                    options.MaxDispatchLatency = (int)(value * 1000);
                    break;
                case "max_duration":
                    if (value < 0)
                        throw SimulatedSqlException.EventSessionOptionInvalid(option, 6);
                    options.MaxDuration = value;
                    break;
                case "max_event_size":
                    if (value >= 2048 * 1024)
                        throw SimulatedSqlException.EventSessionOptionTooLarge(option, 4);
                    options.MaxEventSize = (int)value;
                    break;
                case "max_memory":
                    if (value >= 2048 * 1024)
                        throw SimulatedSqlException.EventSessionOptionTooLarge(option, 2);
                    options.MaxMemory = (int)value;
                    break;
                case "memory_partition_mode":
                    options.PartitionMode = (char)value;
                    break;
                case "startup_state":
                    options.StartupState = value != 0;
                    break;
                default:
                    options.TrackCausality = value != 0;
                    break;
            }
        }
        if (options.MaxMemory != 0 && options.MaxMemory * 1024L < 195_993)
            throw SimulatedSqlException.XeBufferTooSmall();
        // An event size must hold a regular buffer whole (probed 2026-10-16
        // against SQL Server 2025: under 4 MB, 1,400 KB is refused and 1,408
        // KB taken, the buffer being 1,441,587 bytes).
        if (options.MaxEventSize != 0 && options.MaxEventSize * 1024L < options.Buffers().RegularSize)
            throw SimulatedSqlException.XeMaxEventSizeTooSmall();
    }

    /// <summary>
    /// The checks real makes of a whole definition once its events and
    /// targets resolve: an asynchronous target with no memory (Msg 25635),
    /// and an event that never blocks in a session that may lose none
    /// (25643), which an <c>ALTER</c> names with its module for an event the
    /// session already had (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static void ValidateEventSession(EventSessionDefinition definition, SessionEvent[]? existing = null)
    {
        var options = definition.Options;
        if (options.MaxMemory == 0)
        {
            foreach (var target in definition.Targets)
            {
                if (target.Target.Capabilities is { } bits && (bits & 1024) == 0)
                    throw SimulatedSqlException.XeAsynchronousTargetWithoutMemory();
            }
        }
        if (options.RetentionMode == 'N')
        {
            foreach (var sessionEvent in definition.Events)
            {
                if (sessionEvent.Event.CapabilitiesDesc?.Contains("no_block", StringComparison.Ordinal) == true)
                {
                    throw SimulatedSqlException.XeEventNeedsEventLoss(existing is not null && existing.Contains(sessionEvent)
                        ? sessionEvent.Event.Package.ModuleGuid + "." + sessionEvent.Event.QualifiedName
                        : sessionEvent.Event.QualifiedName);
                }
            }
        }
    }

    /// <summary>
    /// Resolves an event clause against the engine's catalog, numbering the
    /// event and then each action and attribute from <paramref name="nextId"/>.
    /// Its actions resolve first, then its attributes, then its predicate
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static SessionEvent BindSessionEvent(EventClause clause, ref int nextId)
    {
        var @event = ResolveXeObject(clause.Name, "event", "event", 1);
        var id = nextId++;
        var actions = new List<XeObject>();
        foreach (var actionName in clause.Actions)
        {
            actions.Add(ResolveXeObject(actionName, "action", "event action", 3));
            nextId++;
        }
        var fields = BindAttributes(clause.Sets, @event, "event", 24);
        nextId += fields.Length;
        string? predicateText = null;
        XePredicate? predicate = null;
        if (clause.Where is { } where)
        {
            predicate = where.Bind(@event);
            predicateText = where.Text;
        }
        return new SessionEvent(id, clause.Name[^1], @event, [.. actions], fields, predicateText, predicate);
    }

    private static SessionTarget BindSessionTarget(TargetClause clause, ref int nextId)
    {
        var target = ResolveXeObject(clause.Name, "target", "target", 2);
        var id = nextId++;
        var fields = BindAttributes(clause.Sets, target, "target", 23);
        nextId += fields.Length;
        foreach (var column in target.Columns)
        {
            if (column.ColumnType == "customizable" && column.CapabilitiesDesc == "mandatory" && !Array.Exists(fields, field => ReferenceEquals(field.Column, column)))
                throw SimulatedSqlException.XeMandatoryAttributeMissing(target.QualifiedName);
        }
        ValidateTargetAttributes(target, fields);
        return new SessionTarget(id, clause.Name[^1], target, fields);
    }

    /// <summary>
    /// The target-specific checks real makes (Msg 25641): an empty file name,
    /// a ring buffer's memory past what a 32-bit byte count holds, and a
    /// histogram whose source names no action, or an event field without the
    /// event it filters on (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static void ValidateTargetAttributes(XeObject target, SessionField[] fields)
    {
        string? Text(string name) => fields.FirstOrDefault(field => field.Column.Name == name)?.Value.XmlText;
        switch (target.Name)
        {
            case "event_file" when Text("filename") is "":
                throw SimulatedSqlException.XeTargetParameterInvalid(target.QualifiedName, "filename", "Target parameter at index 0 is invalid");
            case "histogram":
                var source = Text("source") ?? "";
                if (Text("source_type") is "0")
                {
                    if (string.IsNullOrEmpty(Text("filtering_event_name")))
                        throw SimulatedSqlException.XeTargetParameterInvalid(target.QualifiedName, "filtering_event_name", "A value is required for the parameter 'filtering_event_name' when source type is 0.");
                    break;
                }
                var dot = source.LastIndexOf('.');
                if (dot <= 0 || XeMetadata.Instance.Find(source[..dot], source[(dot + 1)..], "action") is null)
                    throw SimulatedSqlException.XeTargetParameterInvalid(target.QualifiedName, "source", $"The action '{source}' does not exist. Verify that the action exists and try your command again.");
                break;
            case "ring_buffer" when fields.FirstOrDefault(field => field.Column.Name == "max_memory") is { } memory && memory.Value.Unsigned > 4_194_303:
                throw SimulatedSqlException.XeTargetParameterInvalid(target.QualifiedName, "max_memory", "");
        }
    }

    /// <summary>Binds a <c>SET</c> list to the customizable columns of <paramref name="owner"/>.</summary>
    private static SessionField[] BindAttributes(List<(string Name, XeConstant Value)> sets, XeObject owner, string kind, byte repeatState)
    {
        var fields = new List<SessionField>();
        foreach (var (name, constant) in sets)
        {
            var column = owner.Columns.FirstOrDefault(candidate => candidate.ColumnType == "customizable" && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw SimulatedSqlException.XeAttributeMissing(kind, owner.QualifiedName, name);
            if (fields.Exists(field => ReferenceEquals(field.Column, column)))
                throw SimulatedSqlException.XeAttributeSpecifiedMultipleTimes(kind, owner.QualifiedName, name, repeatState);
            var type = XeType.Of(column.TypeName, column.TypePackage);
            var value = constant.ConvertTo(type, forPredicate: false)
                ?? throw SimulatedSqlException.XeValueInvalid("customizable attribute", name, kind, owner.QualifiedName, constant.FailState(type, forPredicate: false));
            fields.Add(new SessionField(name, column, constant.Value, value));
        }
        return [.. fields];
    }

    /// <summary>
    /// Resolves an event, action or target named with or without its package
    /// (and, three-part, its module): a name no package holds, or one real
    /// keeps private, is Msg 25623; an unqualified one two packages hold is
    /// Msg 25658 (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static XeObject ResolveXeObject(List<string> parts, string objectType, string kind, byte state)
    {
        var written = string.Join('.', parts);
        if (parts.Count > 3)
            throw SimulatedSqlException.XeObjectNameInvalid(kind, written, state);
        var name = parts[^1];
        var package = parts.Count >= 2 ? parts[^2] : null;
        var module = parts.Count == 3 ? parts[0] : null;
        XeObject? found = null;
        var matches = 0;
        foreach (var candidate in XeMetadata.Instance.Objects)
        {
            if (candidate.ObjectType != objectType || candidate.IsPrivate || !candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (package is not null && !candidate.Package.Name.Equals(package, StringComparison.OrdinalIgnoreCase))
                continue;
            if (module is not null && !candidate.Package.ModuleGuid.Equals(module, StringComparison.OrdinalIgnoreCase))
                continue;
            found ??= candidate;
            matches++;
        }
        if (found is null)
            throw SimulatedSqlException.XeObjectNameInvalid(kind, written, state);
        if (matches > 1)
            throw SimulatedSqlException.XeEventNameNotUnique(written);
        return found;
    }

    /// <summary>
    /// The event an <c>ALTER … DROP EVENT</c> names, matched by the name the
    /// session stored — exactly as written, so a different case misses —
    /// and its package when given. A miss is Msg 25653 on a running session
    /// and Msg 25706 on a stopped one (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static SessionEvent FindDroppedEvent(List<SessionEvent> events, List<string> name, bool running)
    {
        foreach (var candidate in events)
        {
            if (candidate.Name == name[^1] && (name.Count < 2 || candidate.Event.Package.Name.Equals(name[^2], StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        var written = string.Join('.', name);
        throw running ? SimulatedSqlException.XeObjectNotInSession("event", written) : SimulatedSqlException.XeObjectNotFound("event", written, 4);
    }

    /// <summary>The target an <c>ALTER … DROP TARGET</c> names, resolved against the catalog first.</summary>
    private static SessionTarget FindDroppedTarget(List<SessionTarget> targets, List<string> name, bool running)
    {
        var target = ResolveXeObject(name, "target", "target", 2);
        foreach (var candidate in targets)
        {
            if (ReferenceEquals(candidate.Target, target))
                return candidate;
        }
        var written = string.Join('.', name);
        throw running ? SimulatedSqlException.XeObjectNotInSession("target", written) : SimulatedSqlException.XeObjectNotFound("target", written, 5);
    }

    /// <summary>One <c>CREATE</c> / <c>ALTER EVENT SESSION</c> as parsed, before its names resolve.</summary>
    private sealed class EventSessionStatement(string name)
    {
        public readonly string Name = name;
        public readonly List<EventClause> AddEvents = [];
        public readonly List<List<string>> DropEvents = [];
        public readonly List<TargetClause> AddTargets = [];
        public readonly List<List<string>> DropTargets = [];

        /// <summary>The <c>WITH</c> options in order, lower-cased, each with its value settled to the catalog's unit.</summary>
        public readonly List<(string Option, long Value)> Options = [];

        /// <summary><c>START</c> or <c>STOP</c> for a <c>STATE =</c> clause.</summary>
        public string? State;
    }

    private sealed class EventClause(List<string> name)
    {
        public readonly List<string> Name = name;
        public List<(string Name, XeConstant Value)> Sets = [];
        public readonly List<List<string>> Actions = [];
        public XePredicateSyntax? Where;
    }

    private sealed class TargetClause(List<string> name)
    {
        public readonly List<string> Name = name;
        public List<(string Name, XeConstant Value)> Sets = [];
    }
}

/// <summary>
/// Real's default sessions — <c>system_health</c> and <c>AlwaysOn_health</c>
/// — as SQL Server 2025's catalog lists them (dumped 2026-10-16 into
/// <c>ExtendedEvents/DefaultSessions.tsv</c>), under their own ids. The
/// definitions are immutable, so one parse serves every simulation in the
/// process.
/// </summary>
internal static class DefaultEventSessions
{
    private static EventSessionDefinition[]? definitions;

    public static EventSessionDefinition[] Definitions(BatchContext batch)
    {
        if (Volatile.Read(ref definitions) is { } parsed)
            return parsed;
        var built = Build(batch);
        return Interlocked.CompareExchange(ref definitions, built, null) ?? built;
    }

    private static EventSessionDefinition[] Build(BatchContext batch)
    {
        using var stream = typeof(DefaultEventSessions).Assembly.GetManifestResourceStream("SqlServerSimulator.XeDefaultSessions.tsv")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.XeDefaultSessions.tsv is missing.");
        using var reader = new StreamReader(stream);
        var rows = new List<string[]>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length != 0)
                rows.Add(line.Split('\t'));
        }
        var metadata = XeMetadata.Instance;
        XeObject Resolve(string package, string module, string name, string objectType) =>
            metadata.Objects.First(candidate => candidate.ObjectType == objectType && candidate.Name == name && candidate.Package.Name == package && candidate.Package.ModuleGuid == module);
        int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);

        var result = new List<EventSessionDefinition>();
        foreach (var session in rows.Where(row => row[0] == "S"))
        {
            var id = session[1];
            var options = new EventSessionOptions
            {
                RetentionMode = session[3][0],
                MaxDispatchLatency = Int(session[4]),
                MaxMemory = Int(session[5]),
                MaxEventSize = Int(session[6]),
                PartitionMode = session[7][0],
                TrackCausality = session[8] == "1",
                StartupState = session[9] == "1",
                MaxDuration = Int(session[10]),
            };
            var fieldRows = rows.Where(row => row[0] == "F" && row[1] == id).ToList();
            SessionField[] FieldsOf(int objectId, XeObject owner) => [.. fieldRows.Where(row => Int(row[2]) == objectId).Select(row =>
            {
                var column = owner.Columns.First(candidate => candidate.ColumnType == "customizable" && candidate.Name == row[3]);
                var literal = row[4] == "int" ? SqlValue.FromInt32(Int(row[5])) : SqlValue.FromNVarchar(row[5]);
                var constant = XeConstant.FromValue(literal);
                return new SessionField(row[3], column, literal, constant.ConvertTo(XeType.Of(column.TypeName, column.TypePackage), forPredicate: false)!.Value);
            })];

            var events = new List<SessionEvent>();
            var maxId = 0;
            foreach (var row in rows.Where(row => row[0] == "E" && row[1] == id))
            {
                var eventId = Int(row[2]);
                var @event = Resolve(row[3], row[4], row[5], "event");
                var actions = rows.Where(action => action[0] == "A" && action[1] == id && action[2] == row[2])
                    .Select(action => Resolve(action[3], action[4], action[5], "action")).ToArray();
                string? text = null;
                XePredicate? predicate = null;
                if (row[6] != "\\N")
                {
                    var syntax = ParsePredicate(batch, row[6]);
                    text = syntax.Text;
                    predicate = syntax.Bind(@event);
                }
                events.Add(new SessionEvent(eventId, row[5], @event, actions, FieldsOf(eventId, @event), text, predicate));
                maxId = Math.Max(maxId, eventId + actions.Length);
            }
            var targets = new List<SessionTarget>();
            foreach (var row in rows.Where(row => row[0] == "T" && row[1] == id))
            {
                var targetId = Int(row[2]);
                var target = Resolve(row[3], row[4], row[5], "target");
                var fields = FieldsOf(targetId, target);
                targets.Add(new SessionTarget(targetId, row[5], target, fields));
                maxId = Math.Max(maxId, targetId + fields.Length);
            }
            result.Add(new EventSessionDefinition(Int(id), session[2], options, [.. events], [.. targets], maxId + 1));
        }
        return [.. result];
    }

    /// <summary>Parses a stored predicate text through the session's tokenizer, outside any statement.</summary>
    private static XePredicateSyntax ParsePredicate(BatchContext batch, string text)
    {
        using var command = batch.Connection.CreateCommand();
#pragma warning disable CA2100 // The text is a predicate from the assembly's own default-session resource, never user input.
        command.CommandText = text;
#pragma warning restore CA2100
        var parser = new ParserContext(command, new BatchContext(command, new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer)));
        parser.MoveNextRequired();
        var syntax = XePredicateSyntax.Parse(parser);
        return syntax is XePredicateGroupSyntax group ? group.Inner : syntax;
    }
}
