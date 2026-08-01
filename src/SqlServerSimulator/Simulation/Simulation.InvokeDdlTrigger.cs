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
    /// and when the database carries no DDL trigger at all, which keeps the
    /// per-statement cost of the common case to one dictionary-emptiness test.
    /// </remarks>
    internal static void RecordDdlEvent(
        ParserContext context,
        string eventType,
        string? schemaName,
        string objectName,
        string objectType,
        string? targetObjectName = null,
        string? targetObjectType = null)
    {
        if (context.Batch.IsSkipping || context.Connection.SuppressDdlTriggers || context.CurrentDatabase.DdlTriggers.IsEmpty)
            return;
        var statement = context.Batch.CurrentStatement;
        (statement.PendingDdlEvents ??= []).Add(
            new DdlEventInfo(eventType, schemaName, objectName, objectType, targetObjectName, targetObjectType));
    }

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
    /// Order across several triggers is by <c>object_id</c>, i.e. creation
    /// order, which is what real ran them in without <c>sp_settriggerorder</c>
    /// (whose DATABASE namespace isn't modeled).
    /// </para>
    /// </remarks>
    private void FireDdlTriggers(BatchContext batch)
    {
        var statement = batch.CurrentStatement;
        if (statement.PendingDdlEvents is not { Count: > 0 } events)
            return;
        statement.PendingDdlEvents = null;
        var createdThisStatement = statement.DdlTriggerCreatedThisStatement;

        var database = batch.CurrentDatabase;
        if (database.DdlTriggers.IsEmpty)
            return;

        // The statement's own source text, which every event this statement
        // raised reports as CommandText (probe-confirmed: both DROP_TABLE
        // events of `DROP TABLE a, b` carry the whole statement). The end is
        // the next token's start — the dispatch loop hasn't consumed past the
        // statement — or end of batch for a body-to-end-of-batch statement
        // such as CREATE VIEW.
        var commandText = batch.Parser.Command.CommandText;
        var start = Math.Min(statement.StartIndex, commandText.Length);
        var end = Math.Min(batch.Parser.Token?.StartIndex ?? commandText.Length, commandText.Length);
        var statementText = end > start ? commandText[start..end].TrimEnd() : string.Empty;

        List<(DdlTrigger Trigger, string EventData)>? fires = null;
        foreach (var info in events)
        {
            foreach (var trigger in database.DdlTriggers.Values.OrderBy(t => t.ObjectId))
            {
                if (trigger.ObjectId == createdThisStatement)
                    continue;
                if (trigger.IsDisabled || !trigger.Covers(info.EventType) || !CanFireDdlTrigger(batch, trigger))
                    continue;
                (fires ??= []).Add((trigger, BuildDdlEventData(batch, info, statementText)));
            }
        }
        if (fires is null)
            return;

        _ = RunMutation(batch.Parser, _ =>
        {
            var connection = batch.Connection;
            var outerScopeIdentity = connection.LastIdentity;
            var outerTriggerLog = connection.TriggerStatementUndoLog;
            var outerTriggerVersionEntries = connection.TriggerStatementVersionEntries;
            connection.TriggerStatementUndoLog = batch.CurrentUndoLog;
            connection.TriggerStatementVersionEntries = batch.CurrentStatementVersionEntries;
            try
            {
                foreach (var (trigger, eventData) in fires)
                {
                    RunOneTriggerBody(
                        batch,
                        database,
                        new TriggerFrame(trigger, eventData),
                        trigger.BodyText,
                        trigger.BodyLineOffset,
                        trigger.Name,
                        executeAsClause: null,
                        trigger.ObjectId,
                        countsAsAfterFrame: false,
                        affectedRowCount: 0,
                        trigger.UsesQuotedIdentifier);
                }
            }
            finally
            {
                connection.TriggerStatementUndoLog = outerTriggerLog;
                connection.TriggerStatementVersionEntries = outerTriggerVersionEntries;
            }
            connection.LastIdentity = outerScopeIdentity;
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
        AppendElement(builder, "UserName", connection.Security.Effective.DatabasePrincipalName);
        AppendElement(builder, "DatabaseName", batch.CurrentDatabase.Name);
        if (info.SchemaName is { } schemaName)
            AppendElement(builder, "SchemaName", schemaName);
        AppendElement(builder, "ObjectName", info.ObjectName);
        AppendElement(builder, "ObjectType", info.ObjectType);
        if (info.TargetObjectName is { } targetName)
            AppendElement(builder, "TargetObjectName", targetName);
        if (info.TargetObjectType is { } targetType)
            AppendElement(builder, "TargetObjectType", targetType);
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
}
