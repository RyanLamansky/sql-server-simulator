using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The server-scope triggers (<c>CREATE TRIGGER … ON ALL SERVER</c>):
    /// logon triggers and server-scope DDL triggers, surfaced by
    /// <c>sys.server_triggers</c>. Server-wide, so every database's catalog
    /// views read the same set.
    /// </summary>
    internal readonly ServerTriggerRegistry ServerTriggers = new();

    /// <summary>
    /// The <c>ClientHost</c> a logon trigger's <c>EVENTDATA()</c> reports for
    /// an in-process connection, which has no network peer — real's spelling
    /// for a shared-memory client.
    /// </summary>
    private const string LocalMachineClientHost = "<local machine>";

    /// <summary>
    /// Runs the enabled logon triggers for a session that is opening — an
    /// in-process <see cref="SimulatedDbConnection.Open"/>, a TDS login, or a
    /// pooled connection's reset — and refuses the login with Msg 17892 when
    /// one fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probed 2026-09-28 against SQL Server 2025. The bodies run in
    /// <c>master</c> whatever database the login asked for, as the login's user
    /// there (<c>guest</c> for an unmapped login) unless the trigger's
    /// <c>EXECUTE AS</c> names a login, inside one transaction
    /// (<c>@@TRANCOUNT</c> 1) under <c>XACT_ABORT</c>. A body <c>ROLLBACK</c>,
    /// an error — even one its own <c>CATCH</c> swallowed — and a result set
    /// (Msg 575) all fail the trigger; <c>RAISERROR</c> is exempt, as in a
    /// DML trigger. Nothing the body prints or raises reaches the client: real
    /// writes each message to the error log under the session — a <c>PRINT</c>
    /// or a severity-10 one as its text, an error with its
    /// <c>Error: n, Severity: s, State: t.</c> line first, the error that
    /// failed the trigger included — and logs the refusal under <c>Logon</c>
    /// at severity 20 naming the client, which sees that refusal alone.
    /// </para>
    /// <para>
    /// The no-trigger case costs one array-length read past the login count,
    /// since every session open passes through here.
    /// </para>
    /// </remarks>
    /// <param name="connection">The opening session, its login already stamped.</param>
    /// <param name="isPooled">True for a pooled connection's reset, which <c>EVENTDATA()</c> reports as <c>IsPooled</c> 1.</param>
    internal void FireLogonTriggers(SimulatedDbConnection connection, bool isPooled)
    {
        // Every session open passes through here, which is where the
        // performance counters count logins and pooled resets.
        if (isPooled)
            _ = Interlocked.Increment(ref this.ConnectionResetCount);
        else
            _ = Interlocked.Increment(ref this.LoginCount);
        connection.Session.LoggedIn = 1;
        ExtendedEvents.XeSessionEvents.Login(connection, isPooled);
        var all = this.ServerTriggers.All;
        if (all.Length == 0)
            return;
        List<DdlTrigger>? matched = null;
        foreach (var trigger in all)
        {
            if (!trigger.IsDisabled && trigger.FiresOnLogon)
                (matched ??= []).Add(trigger);
        }
        if (matched is null)
            return;

        var loginName = connection.Security.OriginalLoginName;
        var master = this.Databases[MasterDatabaseName];
        var savedSecurity = connection.Security;
        if (TryMapLoginToDatabaseUser(this, master, loginName, out var principal))
            connection.Security = BuildAuthenticatedSecurityContext(principal, loginName);
        // The session's options at this point are its login defaults, which
        // the bodies can't change for it: a logon trigger runs under NOCOUNT
        // ON that it can't turn off, and its SET LANGUAGE doesn't follow the
        // session out.
        var savedNoCount = connection.NoCount;
        var savedLanguage = connection.Language;
        connection.NoCount = true;
        connection.RunningLogonTriggers = true;
        // The bodies run under a batch of their own with no statement text:
        // nothing in it parses, it only anchors the atomic unit and the
        // per-statement clock the bodies share. Like a client batch it runs on
        // past an error XACT_ABORT exempts — a RAISERROR — which leaves the
        // login standing.
        // Messages the session queued before the bodies ran are the client's.
        var queuedBefore = connection.PendingMessages.Count;
        using var command = new SimulatedDbCommand(this, connection) { CommandText = " " };
        var batch = new BatchContext(command) { ContinueOnError = true };
        var eventData = BuildLogonEventData(batch, loginName, isPooled);
        try
        {
            _ = RunMutation(batch.Parser, _ =>
            {
                var outerTriggerLog = connection.TriggerStatementUndoLog;
                var outerTriggerVersionEntries = connection.TriggerStatementVersionEntries;
                connection.TriggerStatementUndoLog = batch.CurrentUndoLog;
                connection.TriggerStatementVersionEntries = batch.CurrentStatementVersionEntries;
                try
                {
                    foreach (var trigger in OrderScopedTriggers(matched, DdlTrigger.LogonEventType))
                    {
                        RunOneTriggerBody(
                            batch,
                            master,
                            new TriggerFrame(trigger, eventData, DdlTrigger.LogonEventType),
                            trigger.BodyText,
                            trigger.BodyLineOffset,
                            trigger.Name,
                            trigger.ObjectId,
                            countsAsAfterFrame: false,
                            affectedRowCount: 0,
                            trigger.UsesQuotedIdentifier,
                            trigger.UsesAnsiNulls);
                        if (batch.PendingTriggerOutcomes is { } outcomes)
                        {
                            batch.PendingTriggerOutcomes = null;
                            foreach (var outcome in outcomes)
                            {
                                if (outcome is SimulatedSqlResultSet && !connection.LogonUnitCommitted)
                                    throw SimulatedSqlException.LogonTriggerReturnedResultSet();
                                this.LogLogonTriggerOutcome(batch, outcome);
                            }
                        }
                        // A transaction a body opened and left open ends the
                        // trigger as a ROLLBACK does — unless the login's unit
                        // already committed, when it commits too.
                        if (connection.CurrentTransaction is { } leftOpen)
                        {
                            if (connection.LogonUnitCommitted)
                            {
                                leftOpen.TranCount = 1;
                                leftOpen.EndCommit();
                                continue;
                            }
                            leftOpen.EndRollback();
                            throw SimulatedSqlException.TransactionEndedInTrigger(2);
                        }
                    }
                }
                finally
                {
                    connection.TriggerStatementUndoLog = outerTriggerLog;
                    connection.TriggerStatementVersionEntries = outerTriggerVersionEntries;
                }
                return new SimulatedNonQuery(0);
            });
        }
        catch (SimulatedSqlException ended) when (connection.LogonUnitCommitted)
        {
            this.LogLogonTriggerMessages(batch, queuedBefore);
            this.LogLogonTriggerError(batch, ended);
            // Once a body committed the login's unit nothing refuses it: not
            // the Msg 3609 that COMMIT earns, a later error, a result set nor a
            // transaction left open, which commits (probed 2026-09-28 against
            // SQL Server 2025). Any of them ends the remaining bodies.
            if (connection.CurrentTransaction is { } leftOpen)
            {
                if (ended.Number == 3609)
                {
                    leftOpen.TranCount = 1;
                    leftOpen.EndCommit();
                }
                else
                {
                    leftOpen.EndRollback();
                }
            }
        }
        catch (SimulatedSqlException failed)
        {
            this.LogLogonTriggerMessages(batch, queuedBefore);
            this.LogLogonTriggerError(batch, failed);
            this.ErrorLog.WriteError(DateTime.UtcNow, "Logon", 17892, 20, 1,
                $"Logon failed for login '{loginName}' due to trigger execution. [CLIENT: {connection.Transport.Client?.Address.ToString() ?? LocalMachineClient}]");
            var refusal = SimulatedSqlException.LogonFailedDueToTrigger(loginName);
            refusal.PreserveDiagnostics(1, procedure: null);
            throw refusal;
        }
        finally
        {
            this.LogLogonTriggerMessages(batch, queuedBefore);
            connection.Security = savedSecurity;
            connection.NoCount = savedNoCount;
            connection.Language = savedLanguage;
            connection.RunningLogonTriggers = false;
            connection.LogonUnitCommitted = false;
        }
    }

    /// <summary>
    /// Writes what the logon trigger bodies sent and haven't yet been logged:
    /// the outcomes they buffered, then any message they left queued on the
    /// session past the <paramref name="queuedBefore"/> it held already.
    /// </summary>
    private void LogLogonTriggerMessages(BatchContext batch, int queuedBefore)
    {
        if (batch.PendingTriggerOutcomes is { } outcomes)
        {
            batch.PendingTriggerOutcomes = null;
            foreach (var outcome in outcomes)
                this.LogLogonTriggerOutcome(batch, outcome);
        }
        var queue = batch.Connection.PendingMessages;
        if (queue.Count <= queuedBefore)
            return;
        var queued = queue.ToArray();
        queue.Clear();
        for (var i = 0; i < queued.Length; i++)
        {
            if (i < queuedBefore)
                queue.Enqueue(queued[i]);
            else
                this.LogLogonTriggerMessage(batch, queued[i]);
        }
    }

    /// <summary>Writes one outcome of a logon trigger body as real's log has it: a message or a continued-past error.</summary>
    private void LogLogonTriggerOutcome(BatchContext batch, SimulatedStatementOutcome outcome)
    {
        switch (outcome)
        {
            case SimulatedInfoOutcome info:
                this.LogLogonTriggerMessage(batch, info.Message);
                break;
            case SimulatedErrorOutcome error:
                this.LogLogonTriggerError(batch, error.Exception);
                break;
            default:
                break;
        }
    }

    /// <summary>A message is its text alone, a severity-10 one's included; one above is an error.</summary>
    private void LogLogonTriggerMessage(BatchContext batch, SimulatedError message)
    {
        var process = ErrorLog.SessionProcess(batch.Connection.Spid);
        if (message.Class > 10)
            this.ErrorLog.WriteError(DateTime.UtcNow, process, message.Number, message.Class, message.State, message.Message);
        else
            this.ErrorLog.Write(DateTime.UtcNow, process, message.Message);
    }

    /// <summary>An error with its <c>Error: n, Severity: s, State: t.</c> line, each of the errors it carries.</summary>
    private void LogLogonTriggerError(BatchContext batch, SimulatedSqlException error)
    {
        foreach (var each in error.Errors)
            this.LogLogonTriggerMessage(batch, each);
    }

    /// <summary>
    /// The <c>EVENTDATA()</c> document of a <c>LOGON</c> event, in real's
    /// element order (probed 2026-09-28 against SQL Server 2025).
    /// <c>ClientHost</c> is the peer's address for a TDS login, and real's
    /// shared-memory spelling for an in-process one.
    /// </summary>
    private static string BuildLogonEventData(BatchContext batch, string loginName, bool isPooled)
    {
        var connection = batch.Connection;
        var builder = new StringBuilder(320);
        _ = builder.Append("<EVENT_INSTANCE>");
        AppendElement(builder, "EventType", "LOGON");
        AppendElement(builder, "PostTime", batch.CurrentStatement.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
        AppendElement(builder, "SPID", connection.Spid.ToString(CultureInfo.InvariantCulture));
        AppendElement(builder, "ServerName", ServerNameValue);
        AppendElement(builder, "LoginName", loginName);
        AppendElement(builder, "LoginType", "SQL Login");
        AppendElement(builder, "SID", Convert.ToBase64String(LoginSid(loginName)));
        AppendElement(builder, "ClientHost", connection.Transport.Client?.Address.ToString() ?? LocalMachineClientHost);
        AppendElement(builder, "IsPooled", isPooled ? "1" : "0");
        _ = builder.Append("</EVENT_INSTANCE>");
        return builder.ToString();
    }

    /// <summary>
    /// Orders the server-scope triggers an event fires: the one
    /// <c>sp_settriggerorder</c> pinned first, the rest in creation
    /// (<c>object_id</c>) order, then the one pinned last.
    /// </summary>
    private static List<DdlTrigger> OrderScopedTriggers(List<DdlTrigger> triggers, int eventType)
    {
        if (triggers.Count < 2)
            return triggers;
        triggers.Sort((a, b) =>
        {
            var rank = Rank(a).CompareTo(Rank(b));
            return rank != 0 ? rank : a.ObjectId.CompareTo(b.ObjectId);
        });
        return triggers;

        int Rank(DdlTrigger trigger) =>
            trigger.FirstForEvents?.Contains(eventType) == true ? 0
            : trigger.LastForEvents?.Contains(eventType) == true ? 2
            : 1;
    }

    /// <summary>
    /// Pushes a server-scope trigger's <c>WITH EXECUTE AS</c> login for its
    /// body: the login's user in <paramref name="database"/> — <c>dbo</c> for a
    /// sysadmin — with the login itself as <c>SUSER_SNAME()</c>, while
    /// <c>ORIGINAL_LOGIN()</c> keeps the session's (probed 2026-09-28 against
    /// SQL Server 2025).
    /// </summary>
    private static void PushServerTriggerExecuteAsFrame(SimulatedDbConnection connection, string loginName, Database database)
    {
        if (!TryMapLoginToDatabaseUser(connection.Simulation, database, loginName, out var mapped))
            throw SimulatedSqlException.CannotAccessDatabaseUnderSecurityContext(loginName, database.Name, state: 4);
        connection.Security.Push(new SecurityPrincipalFrame(mapped.PrincipalId, mapped.Name, loginName));
    }

    /// <summary>
    /// <c>sp_settriggerorder</c> with <c>@namespace = 'SERVER'</c> or
    /// <c>'DATABASE'</c>: pins a server- or database-scope trigger first or
    /// last among those one event fires. The name resolves in the named scope
    /// only (Msg 15165 otherwise), the event must be a single event rather than
    /// a group (Msg 15600) and one the trigger fires on (Msg 15125), and a slot
    /// another trigger holds is Msg 15130 — probed 2026-09-28 against SQL
    /// Server 2025.
    /// </summary>
    private static void SetScopedTriggerOrder(BatchContext batch, string triggerName, string order, string statementType, bool serverScope)
    {
        var isFirst = BuiltInToken.Equals(order, "First");
        var isLast = BuiltInToken.Equals(order, "Last");
        if (!isFirst && !isLast && !BuiltInToken.Equals(order, "None"))
            throw SimulatedSqlException.InvalidTriggerOrderParameter();

        var leaf = triggerName.Trim('[', ']');
        var simulation = batch.Connection.Simulation;
        DdlTrigger? trigger;
        IEnumerable<DdlTrigger> peers;
        if (serverScope)
        {
            peers = simulation.ServerTriggers.All;
            trigger = simulation.ServerTriggers.TryGetValue(leaf, out var serverTrigger) ? serverTrigger : null;
        }
        else
        {
            peers = batch.CurrentDatabase.DdlTriggers.EnumerateValues();
            trigger = batch.CurrentDatabase.DdlTriggers.TryGetValue(leaf, out var databaseTrigger) ? databaseTrigger : null;
        }
        if (trigger is null)
            throw SimulatedSqlException.CouldNotFindObjectOrNoPermission(triggerName).AtSystemProcedureLine(142);

        // An event group or a name that isn't an event is a bad parameter; an
        // event the trigger doesn't fire on is Msg 15125.
        int eventType;
        bool covered;
        if (BuiltInToken.Equals(statementType, "LOGON"))
        {
            eventType = DdlTrigger.LogonEventType;
            covered = trigger.FiresOnLogon;
        }
        else if (TriggerEventTypes.TryResolve(statementType, out var entry) && !TriggerEventTypes.IsGroup(entry))
        {
            eventType = entry.Type;
            covered = trigger.Covers(entry.TypeName);
        }
        else
        {
            throw SimulatedSqlException.InvalidTriggerOrderParameter();
        }
        if (!covered)
            throw SimulatedSqlException.TriggerIsNotATriggerForAction(triggerName, statementType);

        // Unlike a table trigger's, the conflict lowercases both words.
#pragma warning disable CA1308 // Real's message spells them in lowercase.
        foreach (var peer in peers)
        {
            if (ReferenceEquals(peer, trigger))
                continue;
            if ((isFirst && peer.FirstForEvents?.Contains(eventType) == true) || (isLast && peer.LastForEvents?.Contains(eventType) == true))
                throw SimulatedSqlException.TriggerOrderAlreadyExists(order.ToLowerInvariant(), statementType.ToLowerInvariant());
        }
#pragma warning restore CA1308

        // Setting one slot vacates the other: a trigger can't be both.
        _ = trigger.FirstForEvents?.Remove(eventType);
        _ = trigger.LastForEvents?.Remove(eventType);
        if (isFirst)
            _ = (trigger.FirstForEvents ??= []).Add(eventType);
        else if (isLast)
            _ = (trigger.LastForEvents ??= []).Add(eventType);
    }
}
