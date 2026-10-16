using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

// Event notifications: CREATE / DROP EVENT NOTIFICATION and the delivery of
// each event they take to their target service as an EventNotification
// message, on one dialog per notification (probed 2026-10-16 against SQL
// Server 2025); see docs/claude/service-broker.md.
partial class Simulation
{
    /// <summary>The server's event notifications, replaced whole under <see cref="eventNotificationGate"/> so readers take no lock.</summary>
    internal EventNotification[] ServerEventNotifications = [];

    /// <summary>Serializes the copy-on-write of every notification array, the server's and each database's.</summary>
    private readonly Lock eventNotificationGate = new();

    /// <summary>
    /// The queues a <c>QUEUE_ACTIVATION</c> notification has told of messages
    /// no reader took yet, which tell again only once a <c>RECEIVE</c> takes
    /// from them.
    /// </summary>
    private readonly HashSet<ServiceQueue> eventNotifiedQueues = [];

    private const string EventNotificationServiceName = "http://schemas.microsoft.com/SQL/Notifications/EventNotificationService";

    // ---- CREATE / DROP ----

    /// <summary>
    /// Parses and runs <c>CREATE EVENT NOTIFICATION name ON { SERVER |
    /// DATABASE | QUEUE q } [WITH FAN_IN] FOR event [, …] TO SERVICE 'name',
    /// { 'broker_instance' | 'current database' }</c>, the cursor on
    /// <c>NOTIFICATION</c>. The event list's refusals (Msg 1084, 1098, 1095)
    /// are the batch's compile errors, so they hold in an untaken branch too.
    /// </summary>
    private static void CreateEventNotification(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        context.MoveNextRequired();
        var (parent, queueName) = ReadEventNotificationParent(context);
        var fanIn = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            RequireNextWord(context, "FAN_IN");
            context.MoveNextRequired();
            fanIn = true;
        }
        if (context.Token is not ReservedKeyword { Keyword: Keyword.For })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var events = new List<TriggerEventType>();
        var groups = new List<int?>();
        var invalidForTarget = false;
        do
        {
            context.MoveNextRequired();
            if (context.Token is not Name eventName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (!EventNotificationEventTypes.TryResolve(eventName.Value, out var entry))
                throw SimulatedSqlException.InvalidEventType(eventName.Value);
            invalidForTarget |= parent switch
            {
                EventNotificationParent.Queue => !EventNotificationEventTypes.IsQueueEvent(entry),
                EventNotificationParent.Database => EventNotificationEventTypes.IsQueueEvent(entry) || EventNotificationEventTypes.IsTrace(entry) || !TriggerEventTypes.IsDatabaseLevel(entry),
                _ => EventNotificationEventTypes.IsQueueEvent(entry),
            };
            var group = entry.Type >= TriggerEventTypes.GroupTypeThreshold ? entry.Type : (int?)null;
            foreach (var leaf in EventNotificationEventTypes.Leaves(entry))
            {
                // An event two items name is refused, naming the first leaf
                // the later item repeats (probed 2026-10-16 against SQL Server 2025).
                if (events.Exists(taken => taken.Type == leaf.Type))
                    throw SimulatedSqlException.EventTypeAlreadySpecified(leaf.TypeName);
                events.Add(leaf);
                groups.Add(group);
            }
            context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });
        if (invalidForTarget)
            throw SimulatedSqlException.EventTypeInvalidOnTarget();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.To })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        RequireNextWord(context, "SERVICE");
        context.MoveNextRequired();
        var serviceName = ReadStringOption(context);
        RequireNextOperator(context, ',');
        context.MoveNextRequired();
        var instance = ReadStringOption(context);
        context.MoveNextOptional();
        var batch = context.Batch;
        if (batch.IsSkipping)
            return;

        var connection = context.Connection;
        var simulation = connection.Simulation;
        var database = context.CurrentDatabase;
        ServiceQueue? queue = null;
        if (parent == EventNotificationParent.Queue
            && (!batch.TryResolveQueue(queueName, out queue) || !ReferenceEquals(queue.Schema.Database, database)))
        {
            throw SimulatedSqlException.QueueNotFoundOrDenied(queueName.ToString(), 21);
        }
        // 'current database' names the database the statement runs in, which
        // must carry a broker id (master and model don't).
        if (instance.Equals("current database", StringComparison.OrdinalIgnoreCase))
        {
            if (database.ServiceBrokerGuid == Guid.Empty)
                throw SimulatedSqlException.EventNotificationTargetNotBrokerDatabase(database.Name);
            instance = BrokerInstanceOf(database);
        }
        var permitted = parent switch
        {
            EventNotificationParent.Queue => PermissionEnforcement.HasObjectAlter(batch, database, queue!.ObjectId, queue.SchemaId),
            EventNotificationParent.Database => PermissionEnforcement.HoldsDatabasePermission(batch, database, "CREATE DATABASE DDL EVENT NOTIFICATION"),
            _ => (!events.Exists(static entry => !EventNotificationEventTypes.IsTrace(entry)) || simulation.SessionHoldsServerPermission(connection, Permission.CreateDdlEventNotification))
                && (!events.Exists(static entry => EventNotificationEventTypes.IsTrace(entry)) || simulation.SessionHoldsServerPermission(connection, Permission.CreateTraceEventNotification)),
        };
        if (!permitted)
            throw SimulatedSqlException.EventNotificationPermissionDenied(name);
        var loginName = connection.Security.Effective.LoginName;
        var msdb = simulation.Databases[MsdbDatabaseName];
        // A server's notification sends from msdb, as the login's user there.
        if (parent == EventNotificationParent.Server && !simulation.SessionHoldsServerPermission(connection, Permission.ControlServer)
            && !msdb.Principals.EnumerateValues().Any(principal => principal.LoginName is { } mapped && BuiltInToken.Comparer.Equals(mapped, loginName)))
        {
            throw SimulatedSqlException.EventNotificationNoUser(name, loginName, MsdbDatabaseName, 2);
        }
        if (Array.Exists(parent == EventNotificationParent.Server ? simulation.ServerEventNotifications : database.EventNotifications,
            other => other.Parent == parent && ReferenceEquals(other.Queue, queue) && database.Collation.Equals(other.Name, name)))
        {
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 21);
        }
        if (parent != EventNotificationParent.Server)
            database.RejectWriteWhenReadOnly();

        var home = parent == EventNotificationParent.Server ? msdb : database;
        var objectId = (parent == EventNotificationParent.Server ? simulation.Databases[MasterDatabaseName] : database).AllocateObjectId();
        int principalId;
        if (parent == EventNotificationParent.Server)
            principalId = simulation.TryResolveServerPrincipalId(loginName, out var serverPrincipal) ? serverPrincipal : 1;
        else
            principalId = connection.Security.Effective.DatabasePrincipalId;
        var notification = new EventNotification(name, objectId, parent, home, queue, [.. events], [.. groups], batch.CurrentStatement.UtcNow,
            serviceName, instance, fanIn ? LoginSid(loginName) : null, principalId);
        simulation.OpenNotificationDialog(batch, notification);
        lock (simulation.eventNotificationGate)
        {
            var current = parent == EventNotificationParent.Server ? simulation.ServerEventNotifications : database.EventNotifications;
            simulation.SetEventNotifications(parent == EventNotificationParent.Server ? null : database, [.. current, notification]);
        }
        RecordDdlUndo(context, () =>
        {
            lock (simulation.eventNotificationGate)
            {
                var current = parent == EventNotificationParent.Server ? simulation.ServerEventNotifications : database.EventNotifications;
                simulation.SetEventNotifications(parent == EventNotificationParent.Server ? null : database, [.. current.Where(other => !ReferenceEquals(other, notification))]);
            }
        });
        if (parent != EventNotificationParent.Server)
            RecordDdlEvent(context, "CREATE_EVENT_NOTIFICATION", schemaName: null, name, "EVENT NOTIFICATION", trailingElements: EventNotificationTarget(queue));
        // Messages already waiting on the queue are what a new QUEUE_ACTIVATION notification tells of first.
        if (queue is not null && notification.Takes(EventNotificationEventTypes.QueueActivation))
            OnCommit(batch, () => simulation.NoteQueueActivationEvent(queue, database));
    }

    /// <summary>
    /// The <c>ON SERVER | DATABASE | QUEUE q</c> of a <c>CREATE</c> or
    /// <c>DROP</c>, the cursor on <c>ON</c> and left after what it read.
    /// </summary>
    private static (EventNotificationParent Parent, MultiPartName Queue) ReadEventNotificationParent(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Database })
        {
            context.MoveNextOptional();
            return (EventNotificationParent.Database, default);
        }
        if (AtWord(context, "SERVER"))
        {
            context.MoveNextOptional();
            return (EventNotificationParent.Server, default);
        }
        if (!AtWord(context, "QUEUE"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var queue = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();
        return (EventNotificationParent.Queue, queue);
    }

    /// <summary>The <c>TargetSchemaName</c> / <c>TargetObjectName</c> / <c>TargetObjectType</c> a notification's DDL event carries.</summary>
    private static string EventNotificationTarget(ServiceQueue? queue)
    {
        var elements = new StringBuilder();
        AppendElement(elements, "TargetSchemaName", queue?.Schema.Name ?? "");
        AppendElement(elements, "TargetObjectName", queue?.Name ?? "");
        AppendElement(elements, "TargetObjectType", queue is null ? "Database" : "SERVICE QUEUE");
        return elements.ToString();
    }

    /// <summary>
    /// Parses and runs <c>DROP EVENT NOTIFICATION name [, …] ON { SERVER |
    /// DATABASE | QUEUE q }</c>, the cursor on <c>NOTIFICATION</c>: all of them
    /// or none, a missing one Msg 15151, which ends the batch. Each one's
    /// dialog ends, its far side told with an EndDialog.
    /// </summary>
    private static void DropEventNotification(ParserContext context)
    {
        var names = new List<string>();
        do
        {
            context.MoveNextRequired();
            names.Add(ReadBrokerName(context));
            context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });
        var (parent, queueName) = ReadEventNotificationParent(context);
        var batch = context.Batch;
        if (batch.IsSkipping)
            return;

        var connection = context.Connection;
        var simulation = connection.Simulation;
        var database = context.CurrentDatabase;
        ServiceQueue? queue = null;
        if (parent == EventNotificationParent.Queue
            && (!batch.TryResolveQueue(queueName, out queue) || !ReferenceEquals(queue.Schema.Database, database)))
        {
            throw SimulatedSqlException.QueueNotFoundOrDenied(queueName.ToString(), 21);
        }
        var existing = parent == EventNotificationParent.Server ? simulation.ServerEventNotifications : database.EventNotifications;
        var dropped = new List<EventNotification>(names.Count);
        foreach (var name in names)
        {
            var found = Array.Find(existing, other => other.Parent == parent && ReferenceEquals(other.Queue, queue) && database.Collation.Equals(other.Name, name));
            if (found is null || !MayDropEventNotification(context, database, found))
                throw SimulatedSqlException.EventNotificationNotFound(name);
            dropped.Add(found);
        }
        if (parent != EventNotificationParent.Server)
            database.RejectWriteWhenReadOnly();
        lock (simulation.eventNotificationGate)
        {
            var current = parent == EventNotificationParent.Server ? simulation.ServerEventNotifications : database.EventNotifications;
            simulation.SetEventNotifications(parent == EventNotificationParent.Server ? null : database, [.. current.Where(other => !dropped.Contains(other))]);
        }
        RecordDdlUndo(context, () =>
        {
            lock (simulation.eventNotificationGate)
            {
                var current = parent == EventNotificationParent.Server ? simulation.ServerEventNotifications : database.EventNotifications;
                simulation.SetEventNotifications(parent == EventNotificationParent.Server ? null : database, [.. current, .. dropped]);
            }
        });
        foreach (var notification in dropped)
        {
            OnCommit(batch, () => simulation.CloseNotificationDialog(notification));
            if (parent != EventNotificationParent.Server)
                RecordDdlEvent(context, "DROP_EVENT_NOTIFICATION", schemaName: null, notification.Name, "EVENT NOTIFICATION", trailingElements: EventNotificationTarget(queue));
        }
    }

    /// <summary>
    /// Whether the effective principal may drop <paramref name="notification"/>:
    /// its owner, the holder of <c>ALTER ANY [DATABASE] EVENT NOTIFICATION</c>,
    /// or for a queue's whoever may alter the queue.
    /// </summary>
    private static bool MayDropEventNotification(ParserContext context, Database database, EventNotification notification)
    {
        var batch = context.Batch;
        var connection = context.Connection;
        var simulation = connection.Simulation;
        if (notification.Parent == EventNotificationParent.Server)
        {
            return simulation.SessionHoldsServerPermission(connection, Permission.AlterAnyEventNotification)
                || (simulation.TryResolveServerPrincipalId(connection.Security.Effective.LoginName, out var principalId) && principalId == notification.PrincipalId);
        }
        return PermissionEnforcement.MayActAs(batch, database, notification.PrincipalId)
            || PermissionEnforcement.HoldsDatabasePermission(batch, database, "ALTER ANY DATABASE EVENT NOTIFICATION")
            || (notification.Queue is { } queue && PermissionEnforcement.HasObjectAlter(batch, database, queue.ObjectId, queue.SchemaId));
    }

    /// <summary>Puts <paramref name="next"/> in place of a database's notifications, or (for null) the server's.</summary>
    private void SetEventNotifications(Database? database, EventNotification[] next)
    {
        lock (this.eventNotificationGate)
        {
            if (database is null)
                Volatile.Write(ref this.ServerEventNotifications, next);
            else
                Volatile.Write(ref database.EventNotifications, next);
        }
    }

    /// <summary>Runs <paramref name="action"/> once the statement <paramref name="batch"/> runs commits, or at once outside a transaction.</summary>
    private static void OnCommit(BatchContext batch, Action action)
    {
        if ((batch.CurrentUndoLog ?? batch.Connection.CurrentTransaction?.UndoLog) is { } log)
            log.RecordBrokerChange(static () => { }, action);
        else
            action();
    }

    // ---- the dialog ----

    /// <summary>
    /// Opens <paramref name="notification"/>'s dialog: the initiator's endpoint,
    /// <c>STARTED_OUTBOUND</c>, of its home database's
    /// <c>EventNotificationService</c> under <c>PostEventNotification</c>, in
    /// a conversation group of its own; with a <paramref name="batch"/>, pending on its transaction.
    /// </summary>
    private void OpenNotificationDialog(BatchContext? batch, EventNotification notification)
    {
        var home = notification.Home;
        var objects = home.ServiceBroker;
        var service = objects.Services[EventNotificationServiceName];
        var contract = objects.Contracts["http://schemas.microsoft.com/SQL/Notifications/PostEventNotification"];
        var broker = this.Broker;
        lock (broker.Gate)
        {
            var owner = batch is null ? null : BrokerRuntime.PendingOwnerFor(batch);
            var group = new ConversationGroup(broker.NewId(), home, service) { PendingOwner = owner };
            var endpoint = new ConversationEndpoint(broker.NewId(), Guid.NewGuid(), isInitiator: true, home, service, contract, group, notification.ServiceName,
                Database.DboPrincipalId, DateTime.MaxValue.AddDays(-1), encrypted: false)
            {
                PendingOwner = owner,
            };
            broker.Groups[group.Key] = group;
            broker.Endpoints[endpoint.Key] = endpoint;
            notification.Dialog = endpoint;
            if (batch is not null)
            {
                broker.Log(batch, () =>
                {
                    _ = broker.Endpoints.Remove(endpoint.Key);
                    _ = broker.Groups.Remove(group.Key);
                }, () =>
                {
                    endpoint.PendingOwner = null;
                    group.PendingOwner = null;
                });
            }
        }
    }

    /// <summary>
    /// Ends a dropped notification's dialog: the far side gets an EndDialog
    /// and moves to <c>DISCONNECTED_INBOUND</c>, the initiator's endpoint and
    /// what it still held go (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private void CloseNotificationDialog(EventNotification notification)
    {
        if (notification.Dialog is not { } from || this.BrokerIfUsed is not { } broker)
            return;
        lock (broker.Gate)
        {
            if (from.Far is { State: EndpointState.Conversing } far)
            {
                far.State = EndpointState.DisconnectedInbound;
                _ = broker.Enqueue(null, far, far.Database.ServiceBroker.EndDialogType, from.SendSequence, null, DateTime.UtcNow);
                far.ReceiveSequence++;
            }
            _ = broker.Endpoints.Remove(from.Key);
            _ = broker.Groups.Remove(from.Group.Key);
            _ = broker.Transmission.RemoveAll(message => ReferenceEquals(message.From, from));
            Monitor.PulseAll(broker.Gate);
        }
    }

    /// <summary>
    /// The database a notification's broker instance names: the one carrying
    /// that broker id, an enabled one ahead of a copy sharing it; null when
    /// none does.
    /// </summary>
    private Database? DatabaseOfBrokerInstance(string instance)
    {
        Database? found = null;
        foreach (var (_, database) in this.Databases)
        {
            if (database.ServiceBrokerGuid == Guid.Empty || !BrokerInstanceOf(database).Equals(instance, StringComparison.OrdinalIgnoreCase))
                continue;
            if (database.BrokerEnabled)
                return database;
            found ??= database;
        }
        return found;
    }

    /// <summary>
    /// Sends one EventNotification message carrying <paramref name="eventData"/>
    /// on <paramref name="notification"/>'s dialog, outside any transaction:
    /// the first one routes the dialog to its service and opens the target's
    /// endpoint; one its service can't take waits on the transmission queue,
    /// as a dialog's own messages do.
    /// </summary>
    private void SendEventNotification(EventNotification notification, string eventData)
    {
        var body = Encoding.Unicode.GetBytes("\uFEFF" + eventData);
        var broker = this.Broker;
        var now = DateTime.UtcNow;
        lock (broker.Gate)
        {
            // NEW_BROKER, or a restore of the database, ended the dialog: the next event begins another.
            if (notification.Dialog is not { } existing || !broker.Endpoints.ContainsKey(existing.Key))
                this.OpenNotificationDialog(null, notification);
            var from = notification.Dialog!;
            if (from.State is EndpointState.Error or EndpointState.Closed)
                return;
            var type = from.Database.ServiceBroker.MessageTypes["http://schemas.microsoft.com/SQL/Notifications/EventNotification"];
            var sequence = from.SendSequence++;
            if (from.HeldStatus is { } held)
            {
                HoldNotification(broker, from, type, sequence, body, now, held);
                return;
            }
            if (!from.Database.BrokerEnabled)
            {
                HoldNotification(broker, from, type, sequence, body, now, BrokerDisabledStatus);
                return;
            }
            if (from.Far is not { } far)
            {
                var target = this.DatabaseOfBrokerInstance(notification.BrokerInstance);
                // A service the instance doesn't carry, or one that doesn't take
                // the contract, errors the dialog and holds what it sent (probed
                // 2026-10-16 against SQL Server 2025: the notification's endpoint
                // ERROR, its messages held, nothing on EventNotificationErrorsQueue).
                if (target?.ServiceBroker.Services.GetValueOrDefault(notification.ServiceName) is not { } service
                    || !target.ServiceBroker.Contracts.TryGetValue(from.Contract.Name, out var contract) || Array.IndexOf(service.Contracts, contract) < 0)
                {
                    from.State = EndpointState.Error;
                    from.HeldStatus = HeldAfterError;
                    HoldNotification(broker, from, type, sequence, body, now, HeldAfterError);
                    return;
                }
                var group = new ConversationGroup(broker.NewId(), target, service);
                far = new ConversationEndpoint(broker.NewId(), from.ConversationId, isInitiator: false, target, service, contract, group, from.Service.Name,
                    Database.DboPrincipalId, from.Lifetime, encrypted: false)
                {
                    State = EndpointState.Conversing,
                    FarPrincipalId = from.PrincipalId,
                    FarBrokerInstance = BrokerInstanceOf(from.Database),
                    Far = from,
                };
                broker.Groups[group.Key] = group;
                broker.Endpoints[far.Key] = far;
                from.Far = far;
                from.FarBrokerInstance = BrokerInstanceOf(target);
                from.State = EndpointState.Conversing;
            }
            if (!far.Service.Queue.Settings.Enabled)
            {
                HoldNotification(broker, from, type, sequence, body, now, QueueRefusedStatus);
                return;
            }
            _ = broker.Enqueue(null, far, type, sequence, body, now);
            far.ReceiveSequence++;
            Monitor.PulseAll(broker.Gate);
        }
    }

    private static void HoldNotification(BrokerRuntime broker, ConversationEndpoint from, BrokerMessageType type, long sequence, byte[] body, DateTime now, string status)
    {
        broker.Transmission.Add(new TransmissionMessage
        {
            From = from,
            Sequence = sequence,
            MessageTypeName = type.Name,
            Body = body,
            EnqueueTime = now,
            Status = status,
        });
    }

    // ---- the events ----

    /// <summary>
    /// Sends <paramref name="eventData"/> on <paramref name="notification"/>'s
    /// dialog once <paramref name="batch"/>'s statement commits — a DDL event a
    /// rollback undoes tells no one (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private void NotifyOnCommit(BatchContext batch, EventNotification notification, string eventData) =>
        OnCommit(batch, () => this.SendEventNotification(notification, eventData));

    /// <summary>
    /// Tells <paramref name="queue"/>'s <c>QUEUE_ACTIVATION</c> notifications
    /// that ready messages wait on it, once until a <c>RECEIVE</c> takes from
    /// it; called as a message commits to it.
    /// </summary>
    internal void NoteQueueActivationEvent(ServiceQueue queue, Database database)
    {
        var notifications = database.EventNotifications;
        if (notifications.Length == 0 || !queue.Settings.Enabled || !this.QueueHasReadyMessages(queue))
            return;
        string? eventData = null;
        foreach (var notification in notifications)
        {
            if (!ReferenceEquals(notification.Queue, queue) || !notification.Takes(EventNotificationEventTypes.QueueActivation))
                continue;
            lock (this.eventNotifiedQueues)
            {
                if (!this.eventNotifiedQueues.Add(queue) && eventData is null)
                    return;
            }
            this.SendEventNotification(notification, eventData ??= QueueEventData("QUEUE_ACTIVATION", queue, database, QueueMonitorSpid, "sa", "dbo"));
        }
    }

    /// <summary>A <c>RECEIVE</c> took from <paramref name="queue"/>, so its next ready message tells its <c>QUEUE_ACTIVATION</c> notifications again.</summary>
    private void NoteQueueEventReceived(ServiceQueue queue)
    {
        lock (this.eventNotifiedQueues)
            _ = this.eventNotifiedQueues.Remove(queue);
    }

    /// <summary>
    /// Tells <paramref name="queue"/>'s <c>BROKER_QUEUE_DISABLED</c>
    /// notifications that its poison-message handling turned it off, in the
    /// name of the session whose rollback did.
    /// </summary>
    internal void NoteQueueDisabledEvent(ServiceQueue queue, Database database, SimulatedDbConnection connection)
    {
        string? eventData = null;
        foreach (var notification in database.EventNotifications)
        {
            if (ReferenceEquals(notification.Queue, queue) && notification.Takes(EventNotificationEventTypes.BrokerQueueDisabled))
            {
                this.SendEventNotification(notification, eventData ??= QueueEventData("BROKER_QUEUE_DISABLED", queue, database, connection.Spid,
                    connection.Security.Effective.LoginName, connection.Security.Effective.DatabasePrincipalName));
            }
        }
    }

    /// <summary>The session id a <c>QUEUE_ACTIVATION</c> event reports: real's queue monitor is a system task.</summary>
    private const int QueueMonitorSpid = 17;

    /// <summary>The <c>EVENT_INSTANCE</c> a queue event carries (probed 2026-10-16 against SQL Server 2025).</summary>
    private static string QueueEventData(string eventType, ServiceQueue queue, Database database, int spid, string loginName, string userName)
    {
        var builder = new StringBuilder("<EVENT_INSTANCE>");
        AppendElement(builder, "EventType", eventType);
        AppendElement(builder, "PostTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
        AppendElement(builder, "SPID", spid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendElement(builder, "ServerName", ServerNameValue);
        AppendElement(builder, "LoginName", loginName);
        AppendElement(builder, "UserName", userName);
        AppendElement(builder, "DatabaseName", database.Name);
        AppendElement(builder, "SchemaName", queue.Schema.Name);
        AppendElement(builder, "ObjectName", queue.Name);
        AppendElement(builder, "ObjectType", "QUEUE");
        return builder.Append("</EVENT_INSTANCE>").ToString();
    }

    /// <summary>
    /// Sends a DDL event to the notifications that take it: the server's for
    /// any event, the database's for one of its own.
    /// </summary>
    private void NotifyDdlEvent(BatchContext batch, Database database, DdlEventInfo info, int eventType, Func<string> eventData)
    {
        foreach (var notification in this.ServerEventNotifications)
        {
            if (notification.Takes(eventType))
                this.NotifyOnCommit(batch, notification, eventData());
        }
        if (info.ServerLevelDatabase is not null)
            return;
        foreach (var notification in database.EventNotifications)
        {
            if (notification.Parent == EventNotificationParent.Database && notification.Takes(eventType))
                this.NotifyOnCommit(batch, notification, eventData());
        }
    }

    // ---- trace events ----

    /// <summary>The <c>EventSequence</c> the next trace event a notification sends carries.</summary>
    private long traceEventSequence;

    /// <summary>
    /// Sends trace event <paramref name="type"/> to the server's notifications
    /// that take it, building its <c>EVENT_INSTANCE</c> only when one does.
    /// </summary>
    private void NotifyTraceEvent(int type, Func<long, string> eventData)
    {
        string? built = null;
        foreach (var notification in this.ServerEventNotifications)
        {
            if (notification.Takes(type))
                this.SendEventNotification(notification, built ??= eventData(Interlocked.Increment(ref this.traceEventSequence)));
        }
    }

    /// <summary>
    /// <c>AUDIT_LOGIN</c> as <paramref name="connection"/> opens — a pooled
    /// reset's <c>EventSubClass</c> 2 — with the fields real's trace event
    /// carries (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal void NoteTraceLogin(SimulatedDbConnection connection, bool pooled)
    {
        if (this.ServerEventNotifications.Length == 0)
            return;
        this.NotifyTraceEvent(1014, sequence =>
        {
            var builder = TraceEventHead("AUDIT_LOGIN", connection.Spid);
            AppendElement(builder, "TextData", ExtendedEvents.XeSessionEvents.OptionsText(connection).Replace("\n", "\r\n", StringComparison.Ordinal));
            AppendElement(builder, "BinaryData", Convert.ToBase64String(new byte[12]));
            AppendSessionElements(builder, connection);
            AppendElement(builder, "StartTime", TraceTime(DateTime.UtcNow));
            AppendElement(builder, "EventSubClass", pooled ? "2" : "1");
            AppendElement(builder, "Success", "1");
            AppendElement(builder, "IntegerData", "4096");
            AppendElement(builder, "ServerName", ServerNameValue);
            AppendElement(builder, "DatabaseName", connection.CurrentDatabase.Name);
            return AppendTraceTail(builder, connection, sequence);
        });
    }

    /// <summary><c>AUDIT_LOGOUT</c> as a session that logged in ends, its duration in microseconds and its counters zero.</summary>
    internal void NoteTraceLogout(SimulatedDbConnection connection)
    {
        if (this.ServerEventNotifications.Length == 0)
            return;
        this.NotifyTraceEvent(1015, sequence =>
        {
            var now = DateTime.UtcNow;
            var builder = TraceEventHead("AUDIT_LOGOUT", connection.Spid);
            AppendSessionElements(builder, connection);
            AppendElement(builder, "Duration", ((long)Math.Max(0, (now - connection.LoginTimeUtc).TotalMicroseconds)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendElement(builder, "StartTime", TraceTime(connection.LoginTimeUtc));
            AppendElement(builder, "EndTime", TraceTime(now));
            AppendElement(builder, "Reads", "0");
            AppendElement(builder, "Writes", "0");
            AppendElement(builder, "CPU", "0");
            AppendElement(builder, "EventSubClass", "1");
            AppendElement(builder, "Success", "1");
            AppendElement(builder, "ServerName", ServerNameValue);
            AppendElement(builder, "DatabaseName", "");
            return AppendTraceTail(builder, connection, sequence);
        });
    }

    /// <summary>
    /// <c>DEADLOCK_GRAPH</c> as the deadlock monitor picks a victim: the graph
    /// in the trace's <c>&lt;deadlock-list&gt;</c> form of the
    /// <c>xml_deadlock_report</c> <paramref name="xmlReport"/> builds.
    /// </summary>
    internal void NoteTraceDeadlockGraph(Func<string> xmlReport, string victimId, DateTime foundUtc)
    {
        if (this.ServerEventNotifications.Length == 0)
            return;
        this.NotifyTraceEvent(1148, sequence =>
        {
            var builder = TraceEventHead("DEADLOCK_GRAPH", DeadlockMonitorSpid);
            // The graph is the element's content as markup, not as text.
            _ = builder.Append("<TextData>").Append(DeadlockListForm(xmlReport(), victimId)).Append("</TextData>");
            AppendElement(builder, "TransactionID", "");
            AppendElement(builder, "LoginName", "sa");
            AppendElement(builder, "StartTime", TraceTime(foundUtc));
            AppendElement(builder, "ServerName", ServerNameValue);
            AppendElement(builder, "LoginSid", Convert.ToBase64String(LoginSid("sa")));
            AppendElement(builder, "EventSequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendElement(builder, "IsSystem", "1");
            AppendElement(builder, "SessionLoginName", "");
            return builder.Append("</EVENT_INSTANCE>").ToString();
        });
    }

    /// <summary>The session id the deadlock monitor's events report.</summary>
    private const int DeadlockMonitorSpid = 3;

    /// <summary>
    /// The trace's form of an <c>xml_deadlock_report</c>: a
    /// <c>&lt;deadlock-list&gt;</c> holding one <c>&lt;deadlock&gt;</c> that names
    /// its victim as an attribute, every line within one level deeper and no
    /// <c>&lt;victim-list&gt;</c> (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static string DeadlockListForm(string report, string victimId)
    {
        var builder = new StringBuilder(report.Length + 256).Append("<deadlock-list>\n <deadlock victim=\"").Append(victimId).Append("\">\n");
        var lines = report.Split('\n');
        var inVictims = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith(" <victim-list>", StringComparison.Ordinal))
            {
                inVictims = true;
                continue;
            }
            if (inVictims)
            {
                inVictims = !line.StartsWith(" </victim-list>", StringComparison.Ordinal);
                continue;
            }
            if (line.StartsWith("</deadlock>", StringComparison.Ordinal))
                break;
            _ = builder.Append(' ').Append(line).Append('\n');
        }
        return builder.Append(" </deadlock>\n</deadlock-list>\n").ToString();
    }

    private static StringBuilder TraceEventHead(string eventType, int spid)
    {
        var builder = new StringBuilder(1024).Append("<EVENT_INSTANCE>");
        AppendElement(builder, "EventType", eventType);
        AppendElement(builder, "PostTime", TraceTime(DateTime.UtcNow));
        AppendElement(builder, "SPID", spid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return builder;
    }

    /// <summary>The session's columns a login event carries, <c>DatabaseID</c> through <c>LoginName</c>.</summary>
    private static void AppendSessionElements(StringBuilder builder, SimulatedDbConnection connection)
    {
        AppendElement(builder, "DatabaseID", connection.CurrentDatabase.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendElement(builder, "NTUserName", "");
        AppendElement(builder, "NTDomainName", "");
        AppendElement(builder, "HostName", connection.ClientHostName);
        var clientProcessId = connection.ClientProcessId;
        AppendElement(builder, "ClientProcessID", clientProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendElement(builder, "ApplicationName", connection.ClientApplicationName);
        AppendElement(builder, "LoginName", connection.Security.OriginalLoginName);
    }

    /// <summary>The columns every session's trace event ends with, <c>LoginSid</c> through <c>GroupID</c>.</summary>
    private static string AppendTraceTail(StringBuilder builder, SimulatedDbConnection connection, long sequence)
    {
        var login = connection.Security.OriginalLoginName;
        AppendElement(builder, "LoginSid", Convert.ToBase64String(LoginSid(login)));
        AppendElement(builder, "RequestID", "0");
        AppendElement(builder, "EventSequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendElement(builder, "Type", "1");
        AppendElement(builder, "IsSystem", "");
        AppendElement(builder, "SessionLoginName", login);
        AppendElement(builder, "GroupID", "2");
        return builder.Append("</EVENT_INSTANCE>").ToString();
    }

    /// <summary>A trace column's time at millisecond precision, the trailing zeros real's xml drops dropped with it.</summary>
    private static string TraceTime(DateTime utc)
    {
        var text = utc.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
        return text.EndsWith('.') ? text[..^1] : text;
    }
}
