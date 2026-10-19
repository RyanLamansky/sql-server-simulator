using SqlServerSimulator.Audit;
using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

// CREATE / ALTER / DROP SERVER AUDIT, SERVER AUDIT SPECIFICATION and DATABASE
// AUDIT SPECIFICATION; see docs/claude/audit.md. Each statement reads its
// whole grammar first — the refusals real makes as the batch compiles — and
// only outside skip mode checks, resolves and changes anything.
partial class Simulation
{
    private AuditServer? audits;

    /// <summary>The server's audit state, made on first use.</summary>
    internal AuditServer AuditsFor()
    {
        if (Volatile.Read(ref this.audits) is { } existing)
            return existing;
        var created = new AuditServer(this);
        return Interlocked.CompareExchange(ref this.audits, created, null) ?? created;
    }

    /// <summary>The server's audit state if any statement has made it, null before.</summary>
    internal AuditServer? AuditsIfMade => Volatile.Read(ref this.audits);

    /// <summary>
    /// The audits running, in <c>audit_id</c> order, published whole on every
    /// start and stop; null while none runs, so an audited site's whole cost
    /// on a server auditing nothing is this one read.
    /// </summary>
    internal volatile RunningAudit[]? RunningAudits;

    /// <summary>
    /// Parses the audit DDL statement at the cursor (on <paramref name="verb"/>)
    /// when the words after it are <c>SERVER AUDIT [SPECIFICATION]</c> or
    /// <c>DATABASE AUDIT SPECIFICATION</c>; false, the cursor left on the
    /// verb, for any other statement.
    /// </summary>
    private static bool TryParseAuditDdl(ParserContext context, string verb)
    {
        var checkpoint = context.SaveCheckpoint();
        var first = context.GetNextOptional();
        var second = context.GetNextOptional();
        var third = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        if (!IsWord(second, "AUDIT"))
            return false;
        var specification = IsWord(third, "SPECIFICATION");
        var server = IsWord(first, "SERVER");
        if (!server && !(first is ReservedKeyword { Keyword: Keyword.Database } && specification))
            return false;
        context.MoveNextRequired();
        context.MoveNextRequired();
        if (specification)
            context.MoveNextRequired();
        if (!specification)
        {
            switch (verb)
            {
                case "ALTER":
                    ParseAlterServerAudit(context);
                    break;
                case "CREATE":
                    ParseCreateServerAudit(context);
                    break;
                default:
                    ParseDropServerAudit(context);
                    break;
            }
            return true;
        }
        if (verb == "DROP")
            ParseDropAuditSpecification(context, database: !server);
        else
            ParseCreateOrAlterAuditSpecification(context, database: !server, alter: verb == "ALTER");
        return true;

        static bool IsWord(Token? token, string word) => token is UnquotedString { Value: var text } && text.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A server audit statement's clauses as written.</summary>
    private sealed class ServerAuditClauses
    {
        public AuditDestination? Destination;
        public string? FilePath;
        public long? MaxSize;
        public int? MaxRolloverFiles;
        public int? MaxFiles;
        public bool? ReserveDiskSpace;
        public int? QueueDelay;
        public byte? OnFailure;
        public string? AuditGuid;
        public bool? State;
        public bool HasOptions;
        public XePredicateSyntax? Where;
        public bool RemoveWhere;
        public string? NewName;
    }

    /// <summary>
    /// The audit's name after <c>AUDIT</c>: a <c>#</c> name is Msg 1074, as
    /// for a temporary function (probed 2026-10-17 against SQL Server 2025).
    /// Cursor on the name on return.
    /// </summary>
    private static string ParseAuditName(ParserContext context)
    {
        if (context.GetNextRequired() is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (name.Value.StartsWith('#'))
            throw SimulatedSqlException.TemporaryFunctionNotAllowed(scalar: false);
        return name.Value;
    }

    /// <summary>
    /// <c>CREATE SERVER AUDIT name TO … [WITH (…)] [WHERE …]</c>: without
    /// <c>TO</c> Msg 102 near <c>AUDIT</c>, and a <c>FILE</c> target without
    /// <c>FILEPATH</c> Msg 102 near the name.
    /// </summary>
    private static void ParseCreateServerAudit(ParserContext context)
    {
        var auditWord = context.Token;
        var name = ParseAuditName(context);
        var nameToken = context.Token;
        context.MoveNextOptional();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.To })
            throw SimulatedSqlException.SyntaxErrorNear(auditWord);
        var clauses = new ServerAuditClauses();
        ParseAuditTarget(context, clauses);
        if (clauses.Destination == AuditDestination.File && clauses.FilePath is null)
            throw SimulatedSqlException.SyntaxErrorNear(nameToken);
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            ParseAuditOptions(context, clauses, alter: false);
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            clauses.Where = XePredicateSyntax.Parse(context);
        }
        RequireAuditStatementEnd(context);
        if (context.Batch.IsSkipping)
            return;

        var simulation = context.Simulation;
        var connection = context.Connection;
        RequireMaster(context, "create", "server audit", 1);
        if (!simulation.SessionHoldsServerPermission(connection, Permission.AlterAnyServerAudit))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        var server = simulation.AuditsFor();
        var id = server.AllocateAuditId();
        ValidateAuditClauses(clauses, alterState: 1);
        if (clauses.OnFailure == 1 && !simulation.SessionHoldsServerPermission(connection, Permission.Shutdown))
            throw SimulatedSqlException.AuditShutdownNotPermitted();
        var path = clauses.Destination == AuditDestination.File ? AuditDirectory(simulation, clauses.FilePath!) : "";
        var guid = Guid.NewGuid();
        if (clauses.AuditGuid is { } written)
        {
            if (!Guid.TryParse(written, out var parsed))
                throw SimulatedSqlException.AuditGuidInvalid();
            if (parsed != Guid.Empty)
                guid = parsed;
        }
        if (server.Find(name) is not null || server.FindByGuid(guid) is not null)
            throw SimulatedSqlException.AuditAlreadyExists(name);
        var audit = new ServerAudit(id, name, guid, context.Batch.CurrentStatement.UtcNow, AuditEventsPrincipalId(simulation, connection))
        {
            Destination = clauses.Destination!.Value,
            FilePath = path,
        };
        ApplyAuditClauses(audit, clauses);
        if (clauses.Where is { } where)
        {
            audit.Predicate = where.Bind(AuditEvents.Event);
            audit.PredicateText = where.Text;
        }
        if (!server.TryAdd(audit))
            throw SimulatedSqlException.AuditAlreadyExists(name);
        RecordDdlUndo(context, () => server.Remove(audit));
        RecordServerDdlEvent(context, "CREATE_SERVER_AUDIT", databaseName: null, loginName: null, objectName: name, objectType: "SERVER AUDIT");
        AuditChanged(context, "CR  ", "SERVER AUDIT", "A ", null, name);
    }

    /// <summary>
    /// <c>ALTER SERVER AUDIT name</c> with a new target, options, predicate,
    /// <c>REMOVE WHERE</c>, <c>MODIFY NAME</c>, or <c>WITH (STATE = …)</c>
    /// alone: <c>STATE</c> beside anything else is Msg 33223, and any change
    /// but <c>STATE</c> to a running audit Msg 33071. Starting a running audit
    /// or stopping a stopped one does nothing (probed 2026-10-17 against SQL
    /// Server 2025).
    /// </summary>
    private static void ParseAlterServerAudit(ParserContext context)
    {
        var name = ParseAuditName(context);
        context.MoveNextOptional();
        var clauses = new ServerAuditClauses();
        var otherClauses = 0;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.To }:
                ParseAuditTarget(context, clauses);
                otherClauses++;
                if (context.Token is ReservedKeyword { Keyword: Keyword.With })
                    ParseAuditOptions(context, clauses, alter: true);
                break;
            case ReservedKeyword { Keyword: Keyword.With }:
                ParseAuditOptions(context, clauses, alter: true);
                break;
            case ReservedKeyword { Keyword: Keyword.Where }:
                break;
            case UnquotedString { Value: var remove } when remove.Equals("REMOVE", StringComparison.OrdinalIgnoreCase):
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Where })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                clauses.RemoveWhere = true;
                otherClauses++;
                context.MoveNextOptional();
                break;
            case UnquotedString { Value: var modify } when modify.Equals("MODIFY", StringComparison.OrdinalIgnoreCase):
                if (context.GetNextRequired() is not UnquotedString { Value: var nameWord } || !nameWord.Equals("NAME", StringComparison.OrdinalIgnoreCase)
                    || context.GetNextRequired() is not Operator { Character: '=' })
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                clauses.NewName = ParseAuditName(context);
                otherClauses++;
                context.MoveNextOptional();
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (!clauses.RemoveWhere && clauses.NewName is null && context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            clauses.Where = XePredicateSyntax.Parse(context);
            otherClauses++;
        }
        RequireAuditStatementEnd(context);
        if (context.Batch.IsSkipping)
            return;

        var simulation = context.Simulation;
        var connection = context.Connection;
        if (clauses.State is not null && connection.CurrentTransaction is not null)
            throw SimulatedSqlException.StatementInsideUserTransaction("ALTER SERVER AUDIT", 1);
        RequireMaster(context, "alter", "server audit", 2);
        var server = simulation.AuditsFor();
        if (server.Find(name) is not { } audit || !simulation.SessionHoldsServerPermission(connection, Permission.AlterAnyServerAudit))
            throw SimulatedSqlException.CannotAlterOrDropAudit("alter", name);
        if (clauses.State is { } state)
        {
            if (otherClauses != 0 || clauses.HasOptions)
                throw SimulatedSqlException.AuditStateNotAlone();
            AuditChanged(context, "AL  ", "SERVER AUDIT", "A ", null, audit.Name);
            if (state)
                server.Start(audit, connection, context.Batch.CurrentStatement.UtcNow);
            else
                server.Stop(audit, connection);
            RecordServerDdlEvent(context, "ALTER_SERVER_AUDIT", databaseName: null, loginName: null, objectName: audit.Name, objectType: "SERVER AUDIT");
            return;
        }
        if (audit.Running is not null)
            throw SimulatedSqlException.AuditMustBeDisabled();
        ValidateAuditClauses(clauses, alterState: 2);
        string? path = null;
        if (clauses.FilePath is { } written)
            path = AuditDirectory(simulation, written);
        XePredicate? predicate = null;
        if (clauses.Where is { } where)
            predicate = where.Bind(AuditEvents.Event);
        var saved = audit.Clone();
        var oldName = audit.Name;
        if (clauses.NewName is { } newName)
        {
            if (!server.TryRename(audit, newName))
                throw SimulatedSqlException.AuditAlreadyExists(newName);
            audit.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        }
        if (clauses.Destination is { } destination)
        {
            if (destination == AuditDestination.File && audit.Destination != AuditDestination.File)
            {
                audit.MaxSize = 0;
                audit.MaxRolloverFiles = int.MaxValue;
                audit.MaxFiles = 0;
                audit.ReserveDiskSpace = false;
                audit.FilePath = "";
            }
            audit.Destination = destination;
        }
        if (path is not null)
            audit.FilePath = path;
        ApplyAuditClauses(audit, clauses);
        if (clauses.RemoveWhere)
        {
            audit.Predicate = null;
            audit.PredicateText = null;
        }
        else if (clauses.Where is { } bound)
        {
            audit.Predicate = predicate;
            audit.PredicateText = bound.Text;
        }
        RecordDdlUndo(context, () =>
        {
            if (!BuiltInToken.Comparer.Equals(audit.Name, saved.Name))
                _ = server.TryRename(audit, saved.Name);
            audit.Restore(saved);
        });
        RecordServerDdlEvent(context, "ALTER_SERVER_AUDIT", databaseName: null, loginName: null, objectName: audit.Name, objectType: "SERVER AUDIT");
        AuditChanged(context, "AL  ", "SERVER AUDIT", "A ", null, oldName);
    }

    /// <summary><c>DROP SERVER AUDIT name</c>: a running audit is Msg 33071; the specifications naming it stay, pointing at its GUID.</summary>
    private static void ParseDropServerAudit(ParserContext context)
    {
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();
        RequireAuditStatementEnd(context);
        if (context.Batch.IsSkipping)
            return;
        var simulation = context.Simulation;
        RequireMaster(context, "drop", "server audit", 3);
        var server = simulation.AuditsFor();
        if (server.Find(name) is not { } audit || !simulation.SessionHoldsServerPermission(context.Connection, Permission.AlterAnyServerAudit))
            throw SimulatedSqlException.CannotAlterOrDropAudit("drop", name);
        if (audit.Running is not null)
            throw SimulatedSqlException.AuditMustBeDisabled();
        server.Remove(audit);
        RecordDdlUndo(context, () => _ = server.TryAdd(audit));
        RecordServerDdlEvent(context, "DROP_SERVER_AUDIT", databaseName: null, loginName: null, objectName: audit.Name, objectType: "SERVER AUDIT");
        AuditChanged(context, "DR  ", "SERVER AUDIT", "A ", null, audit.Name);
    }

    /// <summary>
    /// <c>TO FILE (…) | APPLICATION_LOG | SECURITY_LOG</c>, the cursor on
    /// <c>TO</c> on entry and past the target on return. <c>URL</c> and
    /// <c>EXTERNAL_MONITOR</c>, which a Linux server lacks, are Msg 40517.
    /// </summary>
    private static void ParseAuditTarget(ParserContext context, ServerAuditClauses clauses)
    {
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.File }:
                clauses.Destination = AuditDestination.File;
                ParseAuditFileOptions(context, clauses);
                return;
            case UnquotedString { Value: var word } when word.Equals("APPLICATION_LOG", StringComparison.OrdinalIgnoreCase):
                clauses.Destination = AuditDestination.ApplicationLog;
                break;
            case UnquotedString { Value: var word } when word.Equals("SECURITY_LOG", StringComparison.OrdinalIgnoreCase):
                clauses.Destination = AuditDestination.SecurityLog;
                break;
            case UnquotedString { Value: var word } when word.Equals("URL", StringComparison.OrdinalIgnoreCase) || word.Equals("EXTERNAL_MONITOR", StringComparison.OrdinalIgnoreCase):
                throw SimulatedSqlException.AuditOptionNotSupported(word);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
    }

    /// <summary>
    /// <c>( FILEPATH = '…' | MAXSIZE = n {MB|GB|TB} | UNLIMITED |
    /// MAX_ROLLOVER_FILES = n | UNLIMITED | MAX_FILES = n |
    /// RESERVE_DISK_SPACE = ON | OFF [, …] )</c>, a repeat's last value
    /// standing. A size without its unit is Msg 102 near <c>MAXSIZE</c>, a
    /// number past <c>int</c> or with a fraction Msg 1080, and an option the
    /// grammar lacks (<c>RETENTION_DAYS</c> on Linux) Msg 102 near it (probed
    /// 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static void ParseAuditFileOptions(ParserContext context, ServerAuditClauses clauses)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        while (true)
        {
            if (context.Token is not UnquotedString { Value: var option } optionToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var value = context.GetNextRequired();
            if (option.Equals("FILEPATH", StringComparison.OrdinalIgnoreCase))
            {
                if (value is not Literal { Value: var text } || !Storage.SqlType.IsStringCategory(text.Type) || text.IsNull)
                    throw SimulatedSqlException.SyntaxErrorNear(optionToken);
                clauses.FilePath = text.AsString;
                context.MoveNextRequired();
            }
            else if (option.Equals("MAXSIZE", StringComparison.OrdinalIgnoreCase))
            {
                if (IsUnlimited(value))
                {
                    clauses.MaxSize = 0;
                    context.MoveNextRequired();
                }
                else
                {
                    var amount = AuditInteger(context, value);
                    var unit = context.GetNextRequired();
                    if (unit is not UnquotedString { Value: var unitText })
                        throw SimulatedSqlException.SyntaxErrorNear(optionToken);
                    clauses.MaxSize = unitText.Equals("MB", StringComparison.OrdinalIgnoreCase) ? amount
                        : unitText.Equals("GB", StringComparison.OrdinalIgnoreCase) ? amount * 1024L
                        : unitText.Equals("TB", StringComparison.OrdinalIgnoreCase) ? amount * 1024L * 1024L
                        : throw SimulatedSqlException.SyntaxErrorNear(unit);
                    context.MoveNextRequired();
                }
            }
            else if (option.Equals("MAX_ROLLOVER_FILES", StringComparison.OrdinalIgnoreCase))
            {
                clauses.MaxRolloverFiles = IsUnlimited(value) ? int.MaxValue : AuditInteger(context, value);
                context.MoveNextRequired();
            }
            else if (option.Equals("MAX_FILES", StringComparison.OrdinalIgnoreCase))
            {
                if (value is not Numeric)
                    throw SimulatedSqlException.SyntaxErrorNear(optionToken);
                clauses.MaxFiles = AuditInteger(context, value);
                context.MoveNextRequired();
            }
            else if (option.Equals("RESERVE_DISK_SPACE", StringComparison.OrdinalIgnoreCase))
            {
                clauses.ReserveDiskSpace = value switch
                {
                    ReservedKeyword { Keyword: Keyword.On } => true,
                    ReservedKeyword { Keyword: Keyword.Off } => false,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextRequired();
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(optionToken);
            }
            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        context.MoveNextOptional();

        static bool IsUnlimited(Token token) => token is UnquotedString { Value: var word } && word.Equals("UNLIMITED", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An integer option value: one past <c>int</c> or with a fraction is Msg 1080, anything but a number Msg 102 near it.</summary>
    private static int AuditInteger(ParserContext context, Token value) => value switch
    {
        Numeric { Value.Type: Storage.Int32SqlType } number => number.Value.AsInt32,
        Numeric number => throw SimulatedSqlException.IntegerValueOutOfRange(number.ErrorText),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// <c>WITH ( QUEUE_DELAY = n | ON_FAILURE = CONTINUE | SHUTDOWN |
    /// FAIL_OPERATION | AUDIT_GUID = '…' | STATE = ON | OFF [, …] )</c>:
    /// <c>STATE</c> only in <c>ALTER</c> and <c>AUDIT_GUID</c> only in
    /// <c>CREATE</c>, each Msg 102 near it in the other; <c>OPERATOR_AUDIT</c>
    /// is Msg 40517 on a Linux server.
    /// </summary>
    private static void ParseAuditOptions(ParserContext context, ServerAuditClauses clauses, bool alter)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        while (true)
        {
            if (context.Token is not UnquotedString { Value: var option } optionToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (option.Equals("OPERATOR_AUDIT", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.AuditOptionNotSupported(option);
            var isState = option.Equals("STATE", StringComparison.OrdinalIgnoreCase);
            var isGuid = option.Equals("AUDIT_GUID", StringComparison.OrdinalIgnoreCase);
            if ((isState && !alter) || (isGuid && alter))
                throw SimulatedSqlException.SyntaxErrorNear(optionToken);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var value = context.GetNextRequired();
            if (isState)
            {
                clauses.State = value switch
                {
                    ReservedKeyword { Keyword: Keyword.On } => true,
                    ReservedKeyword { Keyword: Keyword.Off } => false,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
            }
            else if (isGuid)
            {
                if (value is not Literal { Value: var text } || !Storage.SqlType.IsStringCategory(text.Type) || text.IsNull)
                    throw SimulatedSqlException.SyntaxErrorNear(optionToken);
                clauses.AuditGuid = text.AsString;
                clauses.HasOptions = true;
            }
            else if (option.Equals("QUEUE_DELAY", StringComparison.OrdinalIgnoreCase))
            {
                if (value is not Numeric { Value.Type: Storage.Int32SqlType })
                {
                    throw value is Numeric whole && !whole.ErrorText.Contains('.', StringComparison.Ordinal)
                        ? SimulatedSqlException.IntegerValueOutOfRange(whole.ErrorText)
                        : SimulatedSqlException.SyntaxErrorNear(context);
                }
                clauses.QueueDelay = ((Numeric)value).Value.AsInt32;
                clauses.HasOptions = true;
            }
            else if (option.Equals("ON_FAILURE", StringComparison.OrdinalIgnoreCase))
            {
                clauses.OnFailure = value switch
                {
                    ReservedKeyword { Keyword: Keyword.Continue } => 0,
                    ReservedKeyword { Keyword: Keyword.Shutdown } => 1,
                    UnquotedString { Value: var failure } when failure.Equals("FAIL_OPERATION", StringComparison.OrdinalIgnoreCase) => 2,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                clauses.HasOptions = true;
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(optionToken);
            }
            context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        context.MoveNextOptional();
    }

    private static void RequireAuditStatementEnd(ParserContext context)
    {
        if (!IsStatementBoundary(context.Token))
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>Server audit DDL outside <c>master</c> is Msg 33074, ahead of any permission check (probed 2026-10-17 against SQL Server 2025).</summary>
    private static void RequireMaster(ParserContext context, string verb, string what, byte state)
    {
        if (!BuiltInToken.Comparer.Equals(context.CurrentDatabase.Name, MasterDatabaseName))
            throw SimulatedSqlException.AuditDdlOutsideMaster(verb, what, state);
    }

    /// <summary>
    /// The option checks real makes as the statement runs, one at a time:
    /// a size of 1 MB (Msg 33070), <c>MAX_FILES</c> beside
    /// <c>MAX_ROLLOVER_FILES</c> (33238), a reservation with no size (33077),
    /// a queue delay from 1 to 999 (33227) and an empty path (15021).
    /// </summary>
    private static void ValidateAuditClauses(ServerAuditClauses clauses, byte alterState)
    {
        if (clauses.MaxSize is > 0 and < 2)
            throw SimulatedSqlException.AuditMaxSizeTooSmall();
        if (clauses.MaxFiles is not null && clauses.MaxRolloverFiles is not null)
            throw SimulatedSqlException.AuditMaxFilesWithRollover();
        if (clauses.ReserveDiskSpace == true && clauses.MaxSize is null or 0)
            throw SimulatedSqlException.AuditReserveDiskSpaceUnlimited();
        if (clauses.QueueDelay is > 0 and < 1000)
            throw SimulatedSqlException.AuditQueueDelayInvalid(alterState);
        if (clauses.FilePath is { Length: 0 })
            throw SimulatedSqlException.AuditFilePathEmpty();
    }

    private static void ApplyAuditClauses(ServerAudit audit, ServerAuditClauses clauses)
    {
        if (clauses.MaxSize is { } size)
            audit.MaxSize = size;
        if (clauses.MaxRolloverFiles is { } rollover)
            audit.MaxRolloverFiles = rollover;
        if (clauses.MaxFiles is { } files)
            audit.MaxFiles = files;
        if (clauses.ReserveDiskSpace is { } reserve)
            audit.ReserveDiskSpace = reserve;
        if (clauses.QueueDelay is { } delay)
            audit.QueueDelay = delay;
        if (clauses.OnFailure is { } failure)
            audit.OnFailure = failure;
    }

    /// <summary>
    /// An audit's <c>FILEPATH</c> as <c>sys.server_file_audits.log_file_path</c>
    /// keeps it: a Linux path with its <c>.</c> and <c>..</c> settled and one
    /// <c>/</c> between names and after the last, a drive path as written with
    /// its separator appended; a path naming no directory the server holds is
    /// Msg 33072 (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static string AuditDirectory(Simulation simulation, string written)
    {
        if (CanonicalServerPath(written) is not { } canonical || !simulation.IsServerDirectory(canonical))
            throw SimulatedSqlException.AuditFilePathInvalid();
        if (written.StartsWith('/'))
            return canonical == "/" ? "/" : canonical + "/";
        return written.EndsWith('\\') || written.EndsWith('/') ? written : written + (written.Contains('\\', StringComparison.Ordinal) ? "\\" : "/");
    }

    /// <summary>The server principal id an audit records as its creator: the session's login's.</summary>
    private static int AuditEventsPrincipalId(Simulation simulation, SimulatedDbConnection connection)
    {
        var login = connection.Security.Effective.LoginName;
        return BuiltInToken.Comparer.Equals(login, "sa") ? 1 : simulation.Logins.TryGetValue(login, out var registered) ? registered.PrincipalId : 1;
    }

    /// <summary>
    /// The <c>AUDIT_CHANGE_GROUP</c> record of an audit or specification
    /// statement: class <c>A </c>, <c>SA</c> or <c>DA</c>, in <c>master</c>
    /// for the server's and the specification's database for a database
    /// specification, naming the object as it was before the statement.
    /// </summary>
    private static void AuditChanged(ParserContext context, string actionId, string classDesc, string classType, Database? database, string name)
    {
        var connection = context.Connection;
        if (connection.Simulation.RunningAudits is null)
            return;
        AuditEvents.Write(connection, new AuditOccurrence(actionId, classDesc, classType)
        {
            Database = database ?? connection.Simulation.Databases[MasterDatabaseName],
            ObjectName = name,
            Statement = AuditEvents.StatementText(context.Batch),
            TransactionId = context.Batch.CurrentTransactionId(),
        });
    }

    /// <summary>
    /// <c>sp_audit_write @user_defined_event_id, @succeeded [,
    /// @user_defined_information]</c>: a <c>UDAU</c> record for every running
    /// audit whose specification takes <c>USER_DEFINED_AUDIT_GROUP</c>; the
    /// first two are required (Msg 201).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpAuditWrite(BatchContext batch, string? returnCodeVariableName)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        Storage.SqlValue? eventId = null, succeeded = null, information = null;
        var positional = 0;
        foreach (var argument in arguments)
        {
            var slot = argument.Name is null ? positional++
                : BuiltInToken.Equals(argument.Name, "user_defined_event_id") ? 0
                : BuiltInToken.Equals(argument.Name, "succeeded") ? 1
                : BuiltInToken.Equals(argument.Name, "user_defined_information") ? 2
                : -1;
            if (argument.IsDefault)
                continue;
            switch (slot)
            {
                case 0:
                    eventId = argument.Value;
                    break;
                case 1:
                    succeeded = argument.Value;
                    break;
                case 2:
                    information = argument.Value;
                    break;
                default:
                    break;
            }
        }
        if (eventId is not { IsNull: false } id)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_audit_write", "@user_defined_event_id");
        if (succeeded is not { IsNull: false } outcome)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_audit_write", "@succeeded");
        AuditSites.UserDefined(batch, id.CoerceTo(Storage.SqlType.SmallInt).AsInt16, outcome.CoerceTo(Storage.SqlType.Bit).AsBoolean,
            information is { IsNull: false } text ? text.CoerceTo(Storage.SqlType.NVarchar).AsString : "");
        SetReturnCode(batch, returnCodeVariableName, 0);
    }

    /// <summary>A specification statement's clauses as written.</summary>
    private sealed class AuditSpecificationClauses
    {
        public string? AuditName;
        public readonly List<(bool Add, AuditAction Action, string? SecurableClass, MultiPartName? Securable, List<string>? Principals)> Changes = [];
        public bool? State;
    }

    /// <summary>
    /// <c>CREATE | ALTER {SERVER | DATABASE} AUDIT SPECIFICATION name
    /// [FOR SERVER AUDIT audit] [{ADD | DROP} (…) [, …]] [WITH (STATE = ON |
    /// OFF [, …])]</c> — <c>FOR</c> required in <c>CREATE</c>, <c>DROP</c>
    /// only in <c>ALTER</c>. A server specification adds groups; a database
    /// one groups or actions on a securable for principals.
    /// </summary>
    private static void ParseCreateOrAlterAuditSpecification(ParserContext context, bool database, bool alter)
    {
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();
        var clauses = new AuditSpecificationClauses();
        if (context.Token is ReservedKeyword { Keyword: Keyword.For })
        {
            if (context.GetNextRequired() is not UnquotedString { Value: var serverWord } || !serverWord.Equals("SERVER", StringComparison.OrdinalIgnoreCase)
                || context.GetNextRequired() is not UnquotedString { Value: var auditWord } || !auditWord.Equals("AUDIT", StringComparison.OrdinalIgnoreCase)
                || context.GetNextRequired() is not Name auditName)
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            clauses.AuditName = auditName.Value;
            context.MoveNextOptional();
        }
        else if (!alter)
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop })
        {
            while (true)
            {
                var add = context.Token is ReservedKeyword { Keyword: Keyword.Add };
                if (!add && !alter)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                ParseAuditSpecificationItem(context, clauses, database, add);
                if (context.Token is not Operator { Character: ',' })
                    break;
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            while (true)
            {
                if (context.Token is not UnquotedString { Value: var option } || !option.Equals("STATE", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                clauses.State = context.GetNextRequired() switch
                {
                    ReservedKeyword { Keyword: Keyword.On } => true,
                    ReservedKeyword { Keyword: Keyword.Off } => false,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextRequired();
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
            context.MoveNextOptional();
        }
        else if (alter && clauses.AuditName is null && clauses.Changes.Count == 0)
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        RequireAuditStatementEnd(context);
        if (context.Batch.IsSkipping)
            return;
        if (alter)
            AlterAuditSpecification(context, database, name, clauses);
        else
            CreateAuditSpecification(context, database, name, clauses);
    }

    /// <summary>
    /// One <c>ADD</c> or <c>DROP</c> item, the cursor on the verb on entry and
    /// past its <c>)</c> on return: a group the specification's scope lacks is
    /// Msg 102 near it, a column list Msg 33211, and a list naming two items
    /// Msg 102 near the comma (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static void ParseAuditSpecificationItem(ParserContext context, AuditSpecificationClauses clauses, bool database, bool add)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var first = context.GetNextRequired();
        var firstName = first switch
        {
            ReservedKeyword keyword => keyword.ErrorText,
            Name word => word.Value,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        var catalog = AuditCatalog.Instance;
        var resolved = (database ? catalog.DatabaseGroupOrAction(firstName) : catalog.ServerGroup(firstName))
            ?? throw SimulatedSqlException.SyntaxErrorNear(first);
        if (resolved.IsGroup)
        {
            if (context.GetNextRequired() is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            clauses.Changes.Add((add, resolved, null, null, null));
            return;
        }
        var actions = new List<AuditAction> { resolved };
        while (true)
        {
            context.MoveNextRequired();
            if (context.Token is Operator { Character: '(' })
                throw SimulatedSqlException.AuditColumnListNotAllowed();
            if (context.Token is not Operator { Character: ',' })
                break;
            var next = context.GetNextRequired();
            var nextName = next switch
            {
                ReservedKeyword keyword => keyword.ErrorText,
                Name word => word.Value,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            if (catalog.DatabaseGroupOrAction(nextName) is not { IsGroup: false } action)
                throw SimulatedSqlException.SyntaxErrorNear(next);
            actions.Add(action);
        }
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var securableClass = ParseAuthorizationClass(context, AuditSecurableClasses);
        var securable = BatchContext.ParseObjectName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var principals = new List<string>();
        while (true)
        {
            principals.Add(context.GetNextRequired() switch
            {
                Name principal => principal.Value,
                ReservedKeyword { Keyword: Keyword.Public } => "public",
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            });
            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        foreach (var action in actions)
            clauses.Changes.Add((add, action, securableClass, securable, principals));
    }

    /// <summary>The securable classes a database audit specification's action takes.</summary>
    private static readonly string[] AuditSecurableClasses = ["DATABASE", "OBJECT", "SCHEMA"];

    /// <summary>
    /// The details <paramref name="clauses"/>' items name, resolved in
    /// <paramref name="database"/>: a missing object, schema or principal is
    /// Msg 15151 — a principal named as a user whatever it is (probed
    /// 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static List<(bool Add, AuditSpecificationDetail Detail)> ResolveAuditSpecificationItems(ParserContext context, Database? database, AuditSpecificationClauses clauses)
    {
        var details = new List<(bool, AuditSpecificationDetail)>();
        foreach (var (add, action, securableClass, securable, principals) in clauses.Changes)
        {
            if (database is null || securable is not { } name)
            {
                details.Add((add, new AuditSpecificationDetail(action, database is null ? (byte)100 : (byte)0, 0, database is null ? 2 : 0)));
                continue;
            }
            byte classId;
            var majorId = 0;
            switch (securableClass)
            {
                case "DATABASE":
                    classId = 0;
                    break;
                case "SCHEMA":
                    classId = 3;
                    if (!database.Schemas.TryGetValue(name.Leaf, out var schema))
                        throw SimulatedSqlException.CannotFindSecurable("schema", name.Leaf);
                    majorId = schema.SchemaId;
                    break;
                default:
                    classId = 1;
                    if (!TryResolveSecurableObject(context.Batch, name, out var resolved))
                        throw SimulatedSqlException.CannotFindSecurable("object", name.Leaf);
                    majorId = resolved.ObjectId;
                    break;
            }
            foreach (var principalName in principals!)
            {
                if (!database.Principals.TryGetValue(principalName, out var principal))
                    throw SimulatedSqlException.CannotFindSecurable("user", principalName);
                details.Add((add, new AuditSpecificationDetail(action, classId, majorId, principal.PrincipalId)));
            }
        }
        return details;
    }

    private static void ApplyAuditSpecificationItems(AuditSpecification specification, List<(bool Add, AuditSpecificationDetail Detail)> details)
    {
        foreach (var (add, detail) in details)
        {
            var at = specification.Details.IndexOf(detail);
            if (add && at < 0)
                specification.Details.Add(detail);
            else if (!add && at >= 0)
                specification.Details.RemoveAt(at);
        }
    }

    /// <summary>
    /// Whether the session may change a database's audit specifications:
    /// <c>ALTER ANY DATABASE AUDIT</c> there, which <c>ALTER ANY SERVER
    /// AUDIT</c> implies.
    /// </summary>
    private static bool MayAlterDatabaseAudit(ParserContext context) =>
        PermissionEnforcement.HoldsDatabasePermission(context.Batch, context.CurrentDatabase, "ALTER ANY DATABASE AUDIT")
        || context.Simulation.SessionHoldsServerPermission(context.Connection, Permission.AlterAnyServerAudit);

    private static void CreateAuditSpecification(ParserContext context, bool database, string name, AuditSpecificationClauses clauses)
    {
        var simulation = context.Simulation;
        var connection = context.Connection;
        if (!database)
            RequireMaster(context, "create", "server audit specification", 4);
        if (!(database ? MayAlterDatabaseAudit(context) : simulation.SessionHoldsServerPermission(connection, Permission.AlterAnyServerAudit)))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        var home = database ? context.CurrentDatabase : simulation.Databases[MasterDatabaseName];
        var details = ResolveAuditSpecificationItems(context, database ? home : null, clauses);
        var id = Interlocked.Increment(ref home.LastAuditSpecificationId);
        var server = simulation.AuditsFor();
        if (server.Find(clauses.AuditName!) is not { } audit)
            throw SimulatedSqlException.AuditNotFound(clauses.AuditName!, 1);
        if (database ? home.AuditSpecifications.ContainsKey(name) : server.FindServerSpecification(name) is not null)
            throw SimulatedSqlException.AuditSpecificationAlreadyExists(name);
        if (database ? DatabaseSpecificationFor(home, audit.Guid, null) is not null : server.ServerSpecificationFor(audit.Guid) is not null)
            throw SimulatedSqlException.AuditSpecificationForAuditExists(audit.Name);
        var specification = new AuditSpecification(id, name, context.Batch.CurrentStatement.UtcNow, audit.Guid) { Enabled = clauses.State == true };
        ApplyAuditSpecificationItems(specification, details);
        if (database)
        {
            if (!home.AuditSpecifications.TryAdd(name, specification))
                throw SimulatedSqlException.AuditSpecificationAlreadyExists(name);
            RecordDdlUndo(context, () => home.AuditSpecifications.TryRemove(name, out _));
            RecordDdlEvent(context, "CREATE_DATABASE_AUDIT_SPECIFICATION", null, name, "DATABASE AUDIT SPECIFICATION");
            AuditChanged(context, "CR  ", "DATABASE AUDIT SPECIFICATION", "DA", home, name);
            return;
        }
        if (!server.TryAddServerSpecification(specification))
            throw SimulatedSqlException.AuditSpecificationAlreadyExists(name);
        RecordDdlUndo(context, () => server.RemoveServerSpecification(specification));
        RecordServerDdlEvent(context, "CREATE_SERVER_AUDIT_SPECIFICATION", databaseName: null, loginName: null, objectName: name, objectType: "SERVER AUDIT SPECIFICATION");
        AuditChanged(context, "CR  ", "SERVER AUDIT SPECIFICATION", "SA", null, name);
    }

    /// <summary>The specification of <paramref name="database"/> writing to <paramref name="guid"/>'s audit, other than <paramref name="except"/>, or null.</summary>
    private static AuditSpecification? DatabaseSpecificationFor(Database database, Guid guid, AuditSpecification? except)
    {
        foreach (var (_, specification) in database.AuditSpecifications)
        {
            if (specification.AuditGuid == guid && !ReferenceEquals(specification, except))
                return specification;
        }
        return null;
    }

    /// <summary>
    /// <c>ALTER … AUDIT SPECIFICATION</c>: <c>STATE</c> inside a user
    /// transaction is Msg 574, any other change to an enabled specification
    /// Msg 33229 — even one the same statement's <c>STATE = OFF</c> would
    /// allow — and dropping an item the specification lacks does nothing
    /// (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static void AlterAuditSpecification(ParserContext context, bool database, string name, AuditSpecificationClauses clauses)
    {
        var simulation = context.Simulation;
        var connection = context.Connection;
        if (clauses.State is not null && connection.CurrentTransaction is not null)
            throw SimulatedSqlException.StatementInsideUserTransaction("ALTER AUDIT SPECIFICATION", 1);
        if (!database)
            RequireMaster(context, "alter", "server audit specification", 4);
        var server = simulation.AuditsFor();
        var home = database ? context.CurrentDatabase : simulation.Databases[MasterDatabaseName];
        var permitted = database ? MayAlterDatabaseAudit(context) : simulation.SessionHoldsServerPermission(connection, Permission.AlterAnyServerAudit);
        var specification = database ? home.AuditSpecifications.GetValueOrDefault(name) : server.FindServerSpecification(name);
        if (specification is null || !permitted)
            throw SimulatedSqlException.AuditSpecificationNotFound(name);
        var changes = clauses.AuditName is not null || clauses.Changes.Count != 0;
        if (changes && specification.Enabled)
            throw SimulatedSqlException.AuditSpecificationMustBeDisabledToChange();
        var details = ResolveAuditSpecificationItems(context, database ? home : null, clauses);
        var auditGuid = specification.AuditGuid;
        if (clauses.AuditName is { } auditName)
        {
            if (server.Find(auditName) is not { } audit)
                throw SimulatedSqlException.AuditNotFound(auditName, 2);
            if (database ? DatabaseSpecificationFor(home, audit.Guid, specification) is not null : server.ServerSpecificationFor(audit.Guid, specification) is not null)
                throw SimulatedSqlException.AuditSpecificationForAuditExists(audit.Name);
            auditGuid = audit.Guid;
        }
        var saved = specification.Clone();
        specification.AuditGuid = auditGuid;
        ApplyAuditSpecificationItems(specification, details);
        if (clauses.State is { } state)
            specification.Enabled = state;
        RecordDdlUndo(context, () => specification.Restore(saved));
        if (database)
        {
            RecordDdlEvent(context, "ALTER_DATABASE_AUDIT_SPECIFICATION", null, name, "DATABASE AUDIT SPECIFICATION");
            AuditChanged(context, "AL  ", "DATABASE AUDIT SPECIFICATION", "DA", home, specification.Name);
        }
        else
        {
            RecordServerDdlEvent(context, "ALTER_SERVER_AUDIT_SPECIFICATION", databaseName: null, loginName: null, objectName: specification.Name, objectType: "SERVER AUDIT SPECIFICATION");
            AuditChanged(context, "AL  ", "SERVER AUDIT SPECIFICATION", "SA", null, specification.Name);
        }
    }

    /// <summary><c>DROP … AUDIT SPECIFICATION name</c>: an enabled one is Msg 33071 state 3.</summary>
    private static void ParseDropAuditSpecification(ParserContext context, bool database)
    {
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();
        RequireAuditStatementEnd(context);
        if (context.Batch.IsSkipping)
            return;
        var simulation = context.Simulation;
        if (!database)
            RequireMaster(context, "drop", "server audit specification", 5);
        var server = simulation.AuditsFor();
        var home = database ? context.CurrentDatabase : simulation.Databases[MasterDatabaseName];
        var permitted = database ? MayAlterDatabaseAudit(context) : simulation.SessionHoldsServerPermission(context.Connection, Permission.AlterAnyServerAudit);
        var specification = database ? home.AuditSpecifications.GetValueOrDefault(name) : server.FindServerSpecification(name);
        if (specification is null || !permitted)
            throw SimulatedSqlException.AuditSpecificationNotFound(name);
        if (specification.Enabled)
            throw SimulatedSqlException.AuditSpecificationMustBeDisabled();
        if (database)
        {
            _ = home.AuditSpecifications.TryRemove(specification.Name, out _);
            RecordDdlUndo(context, () => home.AuditSpecifications.TryAdd(specification.Name, specification));
            RecordDdlEvent(context, "DROP_DATABASE_AUDIT_SPECIFICATION", null, specification.Name, "DATABASE AUDIT SPECIFICATION");
            AuditChanged(context, "DR  ", "DATABASE AUDIT SPECIFICATION", "DA", home, specification.Name);
            return;
        }
        server.RemoveServerSpecification(specification);
        RecordDdlUndo(context, () => server.TryAddServerSpecification(specification));
        RecordServerDdlEvent(context, "DROP_SERVER_AUDIT_SPECIFICATION", databaseName: null, loginName: null, objectName: specification.Name, objectType: "SERVER AUDIT SPECIFICATION");
        AuditChanged(context, "DR  ", "SERVER AUDIT SPECIFICATION", "SA", null, specification.Name);
    }
}
