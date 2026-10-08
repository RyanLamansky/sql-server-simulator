using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Records a DDL event the statement being dispatched just raised. Called
    /// by each modeled DDL processor once its own work succeeded; the dispatch
    /// loop fires the matching database-scope DDL triggers afterwards, so a
    /// statement that throws never raises its event.
    /// </summary>
    /// <remarks>
    /// A no-op in skip mode (an un-taken <c>IF</c> branch never runs its DDL)
    /// and when neither the database nor the server carries a DDL trigger,
    /// which keeps the per-statement cost of the common case to one
    /// dictionary-emptiness test and one array-length read.
    /// </remarks>
    internal static void RecordDdlEvent(
        ParserContext context,
        string eventType,
        string? schemaName,
        string? objectName,
        string? objectType,
        string? targetObjectName = null,
        string? targetObjectType = null,
        string? roleName = null,
        string? ownerName = null,
        string? trailingElements = null,
        int maskStart = -1,
        int maskEnd = -1)
    {
        if (!RaisesDdlEvents(context))
            return;
        var statement = context.Batch.CurrentStatement;
        (statement.PendingDdlEvents ??= []).Add(new DdlEventInfo(
            eventType, schemaName, objectName, objectType, targetObjectName, targetObjectType, roleName, ownerName,
            trailingElements: trailingElements, maskStart: maskStart, maskEnd: maskEnd));
    }

    /// <summary>
    /// Whether <see cref="RecordDdlEvent"/> would record anything, which a site
    /// rendering an event's <c>trailingElements</c> asks first so the common
    /// case — no DDL trigger anywhere — renders nothing.
    /// </summary>
    internal static bool RaisesDdlEvents(ParserContext context) =>
        !context.Batch.IsSkipping && !context.Connection.SuppressDdlTriggers
        && (!context.CurrentDatabase.DdlTriggers.IsEmptyLockFree() || context.Simulation.ServerTriggers.All.Length != 0);

    /// <summary>
    /// Renders a <c>&lt;Parameters&gt;</c> list, one <c>&lt;Param&gt;</c> per
    /// procedure parameter in declaration order, an absent argument as an empty
    /// element — the list an extended-property or binding event carries.
    /// </summary>
    internal static string RenderEventParameters(params ReadOnlySpan<string?> values)
    {
        var builder = new StringBuilder("<Parameters>");
        foreach (var value in values)
            AppendElement(builder, "Param", value ?? "");
        return builder.Append("</Parameters>").ToString();
    }

    /// <summary>
    /// Records a server-level DDL event — one only a server-scope trigger
    /// takes, whose <c>EVENTDATA()</c> carries no <c>UserName</c>: a database
    /// event names its database, a login event the login's own elements.
    /// </summary>
    /// <param name="context">The statement raising it.</param>
    /// <param name="eventType">The <c>sys.trigger_event_types</c> leaf name.</param>
    /// <param name="databaseName">The database a <c>*_DATABASE</c> event names; null for a login event.</param>
    /// <param name="loginName">The login a <c>*_LOGIN</c> event names; null for a database event.</param>
    /// <param name="passwordStart">Where the statement's password literal starts, or -1.</param>
    /// <param name="passwordEnd">Where it ends.</param>
    internal static void RecordServerDdlEvent(
        ParserContext context, string eventType, string? databaseName, string? loginName, int passwordStart = -1, int passwordEnd = -1)
    {
        if (context.Batch.IsSkipping || context.Connection.SuppressDdlTriggers || context.Simulation.ServerTriggers.All.Length == 0)
            return;
        string? loginElements = null;
        if (loginName is not null)
        {
            // The simulator keeps no default database or language per login,
            // so every login reports the server defaults real gives one created
            // without them.
            var elements = new StringBuilder(160);
            AppendElement(elements, "DefaultLanguage", "us_english");
            AppendElement(elements, "DefaultDatabase", MasterDatabaseName);
            AppendElement(elements, "LoginType", "SQL Login");
            AppendElement(elements, "SID", Convert.ToBase64String(LoginSid(loginName)));
            loginElements = elements.ToString();
        }
        (context.Batch.CurrentStatement.PendingDdlEvents ??= []).Add(new DdlEventInfo(
            eventType,
            schemaName: null,
            objectName: loginName,
            objectType: loginName is null ? null : "LOGIN",
            serverLevelDatabase: databaseName ?? "",
            trailingElements: loginElements,
            maskStart: passwordStart,
            maskEnd: passwordEnd));
    }

    /// <summary>
    /// The sid a login reports in <c>EVENTDATA()</c> and <c>SUSER_SID</c>:
    /// the well-known <c>0x01</c> for <c>sa</c>, else its synthetic one.
    /// </summary>
    internal static byte[] LoginSid(string loginName) =>
        BuiltInToken.Comparer.Equals(loginName, "sa") ? [0x01] : BuiltInResources.DeriveLoginSid(loginName);

    /// <summary>
    /// Fires every enabled database-scope DDL trigger matching the events the
    /// statement just recorded. Called from the dispatch loop after the
    /// statement's own outcomes materialize, so the trigger body observes the
    /// completed change (probe-confirmed: <c>OBJECT_ID</c> of the new table
    /// resolves inside a <c>CREATE_TABLE</c> body) and a body error surfaces as
    /// the statement's error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bodies run inside one <see cref="RunMutation"/> scope, so everything
    /// they wrote rolls back together when a later body throws — the same
    /// firing-statement-atomic unit DML triggers get. The DDL itself is not
    /// rolled back; see <c>docs/claude/triggers.md</c>.
    /// </para>
    /// <para>
    /// Order across several triggers of one scope is by <c>object_id</c>, i.e.
    /// creation order, which is what real ran them in, between the ends
    /// <c>sp_settriggerorder</c> pinned.
    /// </para>
    /// </remarks>
    private void FireDdlTriggers(BatchContext batch)
    {
        // Every statement passes through here, and the body's closures would
        // be allocated on entry, so the common case returns ahead of them.
        if (batch.CurrentStatement.PendingDdlEvents is { Count: > 0 } events)
            this.FireDdlTriggers(batch, events);
    }

    /// <summary>Fires the DDL triggers for <paramref name="events"/>, the statement's pending ones.</summary>
    private void FireDdlTriggers(BatchContext batch, List<DdlEventInfo> events)
    {
        var statement = batch.CurrentStatement;
        statement.PendingDdlEvents = null;
        var createdThisStatement = statement.DdlTriggerCreatedThisStatement;

        var database = batch.CurrentDatabase;
        var serverTriggers = this.ServerTriggers.All;
        if (database.DdlTriggers.IsEmptyLockFree() && serverTriggers.Length == 0)
            return;

        // The statement's source text, which every event this statement
        // raised reports as CommandText (probe-confirmed: both DROP_TABLE
        // events of `DROP TABLE a, b` carry the whole statement), over the
        // extent real takes for its kind (see CommandTextExtentOf). The
        // statement's end is the next token's start — the dispatch loop hasn't
        // consumed past the statement — or end of batch for a
        // body-to-end-of-batch statement such as CREATE VIEW.
        var commandText = batch.Parser.Command.CommandText;
        var start = Math.Min(statement.StartIndex, commandText.Length);
        var end = Math.Min(batch.Parser.Token?.StartIndex ?? commandText.Length, commandText.Length);
        var extent = end > start ? CommandTextExtentOf(commandText[start..end]) : CommandTextExtent.Statement;
        switch (extent)
        {
            case CommandTextExtent.Batch:
                start = 0;
                end = commandText.Length;
                break;
            case CommandTextExtent.ThroughSeparator:
                end = Math.Min(NextStatementStart(batch.Parser), commandText.Length);
                break;
        }
        // A statement's own tokens end at its last one: the whitespace,
        // comments and separators after it are no part of it (probed
        // 2026-10-04 against SQL Server 2025 with a trailing comment).
        string Shaped(string text) => extent == CommandTextExtent.Statement ? text[..EndOfLastToken(text)].TrimEnd(';').TrimEnd() : text;
        var statementText = end > start ? Shaped(commandText[start..end]) : string.Empty;

        // Server-scope triggers run ahead of the database's own for the same
        // event (probed 2026-09-28 against SQL Server 2025), and a server-level
        // event reaches them alone.
        List<(DdlTrigger Trigger, string EventData, int EventType)>? fires = null;
        foreach (var info in events)
        {
            var eventType = TriggerEventTypes.TryResolve(info.EventType, out var resolved) ? resolved.Type : 0;
            string? eventData = null;
            var text = info.MaskStart >= start && info.MaskEnd <= end && info.MaskStart < info.MaskEnd
                ? Shaped(string.Concat(commandText.AsSpan(start, info.MaskStart - start), "'******'", commandText.AsSpan(info.MaskEnd, end - info.MaskEnd)))
                : statementText;
            if (serverTriggers.Length != 0)
            {
                List<DdlTrigger>? serverMatched = null;
                foreach (var trigger in serverTriggers)
                {
                    if (!trigger.IsDisabled && trigger.Covers(info.EventType) && CanFireDdlTrigger(batch, trigger))
                        (serverMatched ??= []).Add(trigger);
                }
                if (serverMatched is not null)
                {
                    foreach (var trigger in OrderScopedTriggers(serverMatched, eventType))
                        (fires ??= []).Add((trigger, eventData ??= BuildDdlEventData(batch, info, text), eventType));
                }
            }
            if (info.ServerLevelDatabase is not null)
                continue;
            List<DdlTrigger>? databaseMatched = null;
            foreach (var (_, trigger) in database.DdlTriggers)
            {
                if (trigger.ObjectId == createdThisStatement)
                    continue;
                if (trigger.IsDisabled || !trigger.Covers(info.EventType) || !CanFireDdlTrigger(batch, trigger))
                    continue;
                (databaseMatched ??= []).Add(trigger);
            }
            if (databaseMatched is null)
                continue;
            foreach (var trigger in OrderScopedTriggers(databaseMatched, eventType))
                (fires ??= []).Add((trigger, eventData ??= BuildDdlEventData(batch, info, text), eventType));
        }
        if (fires is null)
            return;

        var master = this.Databases[MasterDatabaseName];
        _ = RunMutation(batch.Parser, _ =>
        {
            var connection = batch.Connection;
            var identityScope = IdentityScope.Enter(connection);
            var outerTriggerLog = connection.TriggerStatementUndoLog;
            var outerTriggerVersionEntries = connection.TriggerStatementVersionEntries;
            connection.TriggerStatementUndoLog = batch.CurrentUndoLog;
            connection.TriggerStatementVersionEntries = batch.CurrentStatementVersionEntries;
            // The DDL the triggers fire for joins their auto-commit unit, so
            // a body's error or ROLLBACK reverses it too.
            if (connection.CurrentTransaction is null && statement.AutocommitDdlUndo is { } ddlUndo && batch.CurrentUndoLog is { } unit)
            {
                foreach (var undo in ddlUndo)
                    unit.RecordSchemaChange(this, undo);
            }
            try
            {
                foreach (var (trigger, eventData, eventType) in fires)
                {
                    RunOneTriggerBody(
                        batch,
                        trigger.IsServerScoped ? master : database,
                        new TriggerFrame(trigger, eventData, eventType),
                        trigger.BodyText,
                        trigger.BodyLineOffset,
                        trigger.Name,
                        trigger.ObjectId,
                        countsAsAfterFrame: false,
                        affectedRowCount: 0,
                        trigger.UsesQuotedIdentifier,
                        trigger.UsesAnsiNulls);
                }
            }
            finally
            {
                connection.TriggerStatementUndoLog = outerTriggerLog;
                connection.TriggerStatementVersionEntries = outerTriggerVersionEntries;
            }
            identityScope.Exit(IdentityScopeKind.Trigger);
            return new SimulatedNonQuery(0);
        });
    }

    /// <summary>
    /// Whether a DDL trigger fires given what's already running. A DDL trigger
    /// nests under another one (probe-confirmed: a <c>CREATE_VIEW</c> trigger
    /// runs at <c>TRIGGER_NESTLEVEL()</c> 2 for a view a <c>CREATE_TABLE</c>
    /// body created) but doesn't re-fire itself for DDL its own body issues —
    /// the innermost-frame test the DML path uses, giving the same
    /// default-<c>RECURSIVE_TRIGGERS</c>-off shape real showed.
    /// </summary>
    /// <remarks>
    /// The <c>nested triggers</c> server option is deliberately not consulted:
    /// it governs AFTER DML triggers, and DDL triggers carry their own
    /// (unmodeled) <c>server trigger recursion</c> knob. DDL frames push
    /// <c>IsAfter = false</c> for the same reason, so a DDL trigger body's DML
    /// still reaches its own AFTER triggers with the option off.
    /// </remarks>
    private static bool CanFireDdlTrigger(BatchContext batch, DdlTrigger trigger)
    {
        var stack = batch.Connection.FiringTriggers;
        return stack.Count == 0 || stack[^1].ObjectId != trigger.ObjectId;
    }

    /// <summary>
    /// Builds the <c>&lt;EVENT_INSTANCE&gt;</c> document <c>EVENTDATA()</c>
    /// returns, in real's element order (probe-confirmed against SQL Server
    /// 2025 for CREATE / ALTER / DROP across every modeled object kind).
    /// </summary>
    /// <remarks>
    /// Modeled subset: the common header (<c>EventType</c> … <c>ObjectType</c>),
    /// the <c>TargetObject*</c> pair the index / trigger / synonym events carry,
    /// and <c>TSQLCommand</c>. Real also emits per-event extras this doesn't —
    /// <c>AlterTableActionList</c>, a principal's <c>SID</c> /
    /// <c>DefaultSchema</c>, a schema's <c>OwnerName</c>.
    /// </remarks>
    private static string BuildDdlEventData(BatchContext batch, DdlEventInfo info, string statementText)
    {
        var connection = batch.Connection;
        var builder = new StringBuilder(256);
        _ = builder.Append("<EVENT_INSTANCE>");
        AppendElement(builder, "EventType", info.EventType);
        // Real stamps local server time to millisecond precision with no zone
        // suffix; the simulator's clock is UTC throughout (see StatementContext).
        AppendElement(builder, "PostTime", batch.CurrentStatement.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
        var spid = connection.Spid;
        AppendElement(builder, "SPID", spid.ToString(CultureInfo.InvariantCulture));
        AppendElement(builder, "ServerName", ServerNameValue);
        AppendElement(builder, "LoginName", connection.Security.Effective.LoginName);
        if (info.ServerLevelDatabase is { } serverLevelDatabase)
        {
            // A server-level event reports no user, and a database it names
            // rather than the session's (probed 2026-09-28 against SQL Server
            // 2025 for the LOGIN and DATABASE events).
            if (serverLevelDatabase.Length != 0)
                AppendElement(builder, "DatabaseName", serverLevelDatabase);
        }
        else
        {
            AppendElement(builder, "UserName", connection.Security.Effective.DatabasePrincipalName);
            AppendElement(builder, "DatabaseName", batch.CurrentDatabase.Name);
        }
        if (info.SchemaName is { } schemaName)
            AppendElement(builder, "SchemaName", schemaName);
        if (info.ObjectName is { } objectName)
            AppendElement(builder, "ObjectName", objectName);
        if (info.ObjectType is { } objectType)
            AppendElement(builder, "ObjectType", objectType);
        if (info.OwnerName is { } ownerName)
            AppendElement(builder, "OwnerName", ownerName);
        if (info.TargetObjectName is { } targetName)
            AppendElement(builder, "TargetObjectName", targetName);
        if (info.TargetObjectType is { } targetType)
            AppendElement(builder, "TargetObjectType", targetType);
        if (info.RoleName is { } roleName)
            AppendElement(builder, "RoleName", roleName);
        if (info.TrailingElements is { } trailingElements)
            _ = builder.Append(trailingElements);
        _ = builder
            .Append("<TSQLCommand><SetOptions ANSI_NULLS=\"ON\" ANSI_NULL_DEFAULT=\"ON\" ANSI_PADDING=\"ON\" QUOTED_IDENTIFIER=\"")
            .Append(connection.QuotedIdentifiers ? "ON" : "OFF")
            .Append("\" ENCRYPTED=\"FALSE\"/>");
        AppendElement(builder, "CommandText", statementText);
        _ = builder.Append("</TSQLCommand></EVENT_INSTANCE>");
        return builder.ToString();
    }

    /// <summary>The <c>ServerName</c> EVENTDATA reports, matching <c>@@SERVERNAME</c>.</summary>
    private const string ServerNameValue = "SIMULATED";

    /// <summary>
    /// The <c>SchemaName</c> a DDL event reports for a written object name: the
    /// qualifier when there is one, else the unqualified fallback every
    /// simulator session resolves against.
    /// </summary>
    internal static string EventSchemaName(MultiPartName name) =>
        name.ImmediateQualifier ?? Database.DefaultSchemaName;

    private static void AppendElement(StringBuilder builder, string name, string value)
    {
        // An empty value is the self-closing form real's xml renders — an
        // ALTER_AUTHORIZATION_DATABASE event's <SchemaName /> for a schema.
        if (value.Length == 0)
        {
            _ = builder.Append('<').Append(name).Append(" />");
            return;
        }
        _ = builder.Append('<').Append(name).Append('>');
        foreach (var c in value)
        {
            _ = c switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                '\r' => builder.Append("&#xD;"),
                _ => builder.Append(c),
            };
        }
        _ = builder.Append("</").Append(name).Append('>');
    }

    /// <summary>
    /// How much source a DDL event's <c>CommandText</c> carries, which real
    /// settles by statement kind (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    private enum CommandTextExtent
    {
        /// <summary>The statement's own tokens, without its <c>;</c>.</summary>
        Statement,

        /// <summary>
        /// Everything up to the next statement's first token, or to the end of
        /// the batch: the <c>;</c>, whitespace and comments between included.
        /// </summary>
        ThroughSeparator,

        /// <summary>The whole batch, leading comments and whitespace included.</summary>
        Batch,
    }

    /// <summary>
    /// The <see cref="CommandTextExtent"/> real reports for the statement
    /// <paramref name="statement"/> begins, read off its leading words: a
    /// module a batch holds alone — <c>CREATE</c> / <c>ALTER</c> of a view,
    /// procedure, function or trigger, and <c>CREATE DEFAULT</c> /
    /// <c>RULE</c> / <c>SCHEMA</c> — reports the batch; the principal and
    /// permission statements and a handful of later kinds report through their
    /// separator; tables, indexes, statistics, databases and the remaining
    /// <c>DROP</c>s report the statement alone.
    /// </summary>
    private static CommandTextExtent CommandTextExtentOf(string statement)
    {
        var words = statement.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
        bool Is(int index, string word) => index < words.Length && string.Equals(words[index], word, StringComparison.OrdinalIgnoreCase);

        if (Is(0, "GRANT") || Is(0, "DENY") || Is(0, "REVOKE"))
            return CommandTextExtent.ThroughSeparator;
        var create = Is(0, "CREATE");
        var alter = Is(0, "ALTER");
        var drop = Is(0, "DROP");
        var kind = create && Is(1, "OR") && Is(2, "ALTER") ? 3 : 1;
        if ((create || alter) && (Is(kind, "VIEW") || Is(kind, "PROC") || Is(kind, "PROCEDURE") || Is(kind, "FUNCTION") || Is(kind, "TRIGGER")))
            return CommandTextExtent.Batch;
        if (create && (Is(1, "DEFAULT") || Is(1, "RULE") || Is(1, "SCHEMA")))
            return CommandTextExtent.Batch;
        return Is(1, "LOGIN") || Is(1, "ROLE") || Is(1, "APPLICATION")
            || ((create || alter) && (Is(1, "USER") || Is(1, "SEQUENCE")))
            || ((create || drop) && Is(1, "TYPE"))
            || (create && Is(1, "SYNONYM"))
            || (drop && (Is(1, "SCHEMA") || Is(1, "STATISTICS")))
            || (alter && (Is(1, "AUTHORIZATION") || (Is(1, "DATABASE") && Is(2, "SCOPED"))))
            || (Is(1, "PARTITION") && (Is(2, "SCHEME") || (create && Is(2, "FUNCTION"))))
            || Is(1, "FULLTEXT")
            ? CommandTextExtent.ThroughSeparator
            : CommandTextExtent.Statement;
    }

    /// <summary>
    /// The end of the last token in <paramref name="text"/> that is neither
    /// whitespace nor a comment, or its length when it doesn't tokenize.
    /// </summary>
    private static int EndOfLastToken(string text)
    {
        var end = 0;
        var index = 0;
        try
        {
            while (Parser.Tokenizer.NextToken(text, ref index, Collation.Baseline) is { } token)
            {
                if (token is not (Parser.Tokens.Whitespace or Parser.Tokens.Comment))
                    end = token.EndIndex;
            }
        }
        catch (SimulatedSqlException)
        {
            return text.Length;
        }
        return end;
    }

    /// <summary>
    /// Where the statement after the one just parsed begins: past the
    /// <c>;</c> separators the cursor sits on, or the end of the batch.
    /// </summary>
    private static int NextStatementStart(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            while (context.Token is Parser.Tokens.Operator { Character: ';' })
                context.MoveNextOptional();
            return context.Token?.StartIndex ?? context.Command.CommandText.Length;
        }
        catch (SimulatedSqlException)
        {
            // Text the tokenizer refuses lies past the separators; the
            // dispatch loop reports it in its own place.
            return context.Token?.StartIndex ?? context.Command.CommandText.Length;
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }
}
