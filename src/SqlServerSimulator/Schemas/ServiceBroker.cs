using System.Collections.Concurrent;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// What a message type requires of a body: the <c>VALIDATION</c> clause of
/// <c>CREATE MESSAGE TYPE</c>, checked as the target's queue takes the message.
/// </summary>
internal enum MessageValidation : byte
{
    None,
    Empty,
    WellFormedXml,
    ValidXml,
}

/// <summary>
/// A Service Broker message type — <c>sys.service_message_types</c>. Its name
/// is compared as written (Latin1_General_BIN), whatever the database's
/// collation (probed 2026-10-16 against SQL Server 2025).
/// </summary>
internal sealed class BrokerMessageType(int id, string name, int principalId, MessageValidation validation, XmlSchemaCollection? collection, bool isSystem)
{
    public readonly int Id = id;
    public readonly string Name = name;

    /// <summary>The owner, <c>sys.service_message_types.principal_id</c>; <c>ALTER AUTHORIZATION</c> moves it.</summary>
    public int PrincipalId = principalId;

    /// <summary>The validation <c>ALTER MESSAGE TYPE</c> replaces, with <see cref="Collection"/>.</summary>
    public MessageValidation Validation = validation;

    /// <summary>The schema collection a <see cref="MessageValidation.ValidXml"/> type checks bodies against.</summary>
    public XmlSchemaCollection? Collection = collection;

    /// <summary>One of the fourteen types every database ships, which no statement drops or alters.</summary>
    public readonly bool IsSystem = isSystem;

    /// <summary>The <c>validation</c> code a queue row and <c>sys.service_message_types</c> report.</summary>
    public char ValidationCode => this.Validation switch
    {
        MessageValidation.Empty => 'E',
        MessageValidation.None => 'N',
        _ => 'X',
    };
}

/// <summary>One message type a contract names, and which side may send it.</summary>
internal readonly struct ContractUsage(BrokerMessageType messageType, bool sentByInitiator, bool sentByTarget)
{
    public readonly BrokerMessageType MessageType = messageType;
    public readonly bool SentByInitiator = sentByInitiator;
    public readonly bool SentByTarget = sentByTarget;
}

/// <summary>A Service Broker contract — <c>sys.service_contracts</c>; a contract is never altered.</summary>
internal sealed class BrokerContract(int id, string name, int principalId, ContractUsage[] usages, bool isSystem)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public int PrincipalId = principalId;
    public readonly ContractUsage[] Usages = usages;
    public readonly bool IsSystem = isSystem;

    /// <summary>The usage naming <paramref name="messageType"/>, if the contract names it.</summary>
    public bool TryFindUsage(BrokerMessageType messageType, out ContractUsage usage)
    {
        foreach (var candidate in this.Usages)
        {
            if (ReferenceEquals(candidate.MessageType, messageType))
            {
                usage = candidate;
                return true;
            }
        }
        usage = default;
        return false;
    }
}

/// <summary>
/// A Service Broker service — <c>sys.services</c>: the queue its messages land
/// on and the contracts it accepts as a target. <c>ALTER SERVICE</c> replaces
/// both in place.
/// </summary>
internal sealed class BrokerService(int id, string name, int principalId, ServiceQueue queue, BrokerContract[] contracts, bool isSystem)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public int PrincipalId = principalId;
    public ServiceQueue Queue = queue;
    public BrokerContract[] Contracts = contracts;
    public readonly bool IsSystem = isSystem;
}

/// <summary>
/// The settings of a <see cref="ServiceQueue"/> that <c>CREATE</c> and
/// <c>ALTER QUEUE</c> write, held as one value so a rolled-back <c>ALTER</c>
/// puts every one back at once.
/// </summary>
internal struct QueueSettings
{
    /// <summary>
    /// <c>STATUS</c>: whether the queue takes <c>RECEIVE</c> and new messages —
    /// <c>is_receive_enabled</c> and <c>is_enqueue_enabled</c> read it alike.
    /// Poison-message handling turns it off.
    /// </summary>
    public bool Enabled;

    public bool Retention;
    public bool PoisonMessageHandling;
    public bool ActivationEnabled;

    /// <summary>The activation procedure as <c>sys.service_queues</c> reports it, <c>[schema].[name]</c>; null without one.</summary>
    public string? ActivationProcedure;

    /// <summary>The schema and name the activation procedure resolves by when it runs.</summary>
    public string? ActivationSchema, ActivationName;

    public short MaxReaders;

    /// <summary>The activation's <c>EXECUTE AS</c>: a principal id, or -2 for <c>OWNER</c>.</summary>
    public int? ExecuteAsPrincipalId;
}

/// <summary>
/// A Service Broker queue — a schema object of type <c>SQ</c>, readable with
/// <c>SELECT</c> and written only by <c>SEND</c> and <c>RECEIVE</c>. Its
/// messages live on <see cref="Messages"/>, guarded by the simulation's
/// <see cref="BrokerRuntime.Gate"/>.
/// </summary>
internal sealed class ServiceQueue(Schema schema, string name, int objectId, DateTime createDate, QueueSettings settings)
    : SchemaObject(name, objectId, schema.SchemaId, createDate)
{
    public Schema Schema = schema;

    public override string ObjectTypeCode => "SQ";
    public override string ObjectTypeDescription => "SERVICE_QUEUE";

    public QueueSettings Settings = settings;

    /// <summary>The queue's messages, in <see cref="QueuedMessage.QueuingOrder"/>; read and written under <see cref="BrokerRuntime.Gate"/>.</summary>
    public readonly List<QueuedMessage> Messages = [];

    /// <summary>The next <c>queuing_order</c> a message takes, which never goes back.</summary>
    public long NextQueuingOrder;

    /// <summary>
    /// How many transactions in a row have rolled back a <c>RECEIVE</c> from
    /// the queue; the fifth disables it when poison-message handling is on.
    /// </summary>
    public int ConsecutiveRollbacks;

    /// <summary>The <c>object_id</c> of the queue's internal message table, <c>queue_messages_&lt;id&gt;</c>.</summary>
    public int InternalTableId;

    /// <summary>How many committed <c>RECEIVE</c>s took messages from the queue, which tells activation a reader made progress.</summary>
    public long ReceivedCount;
}

/// <summary>
/// One message on a <see cref="ServiceQueue"/>: the columns a <c>SELECT</c>
/// of the queue and a <c>RECEIVE</c> read.
/// </summary>
internal sealed class QueuedMessage
{
    /// <summary>0 retained after receipt, 1 ready, 3 retained after sending (the <c>status</c> column).</summary>
    public byte Status = 1;

    public byte Priority = 5;
    public long QueuingOrder;
    public Guid ConversationGroupId;
    public Guid ConversationHandle;
    public long SequenceNumber;
    public string ServiceName = "";
    public int ServiceId;
    public string ContractName = "";
    public int ContractId;
    public string MessageTypeName = "";
    public int MessageTypeId;
    public char Validation;
    public byte[]? Body;
    public DateTime EnqueueTime;

    /// <summary>
    /// The transaction owner that sent the message while its transaction is
    /// open; null once committed. Another session's reads pass over it.
    /// </summary>
    public object? PendingOwner;

    /// <summary>The transaction owner whose open transaction received the message; null otherwise.</summary>
    public object? ReceivedBy;
}

/// <summary>A route — <c>sys.routes</c>; the simulator delivers locally whatever its address says.</summary>
internal sealed class BrokerRoute(int id, string name, int principalId)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public int PrincipalId = principalId;
    public string? RemoteServiceName;
    public string? BrokerInstance;
    public DateTime? Lifetime;
    public string Address = "";
    public string? MirrorAddress;
}

/// <summary>A remote service binding — <c>sys.remote_service_bindings</c>.</summary>
internal sealed class RemoteServiceBinding(int id, string name, int principalId, string remoteServiceName, int remotePrincipalId, bool anonymous)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public int PrincipalId = principalId;
    public readonly string RemoteServiceName = remoteServiceName;
    public int RemotePrincipalId = remotePrincipalId;
    public bool Anonymous = anonymous;
}

/// <summary>A conversation priority — <c>sys.conversation_priorities</c>.</summary>
internal sealed class BrokerPriority(int id, string name)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public int? ContractId;
    public int? LocalServiceId;
    public string? RemoteServiceName;
    public byte Level = 5;
}

/// <summary>
/// One database's Service Broker objects: its message types, contracts,
/// services, routes, remote service bindings and conversation priorities,
/// keyed as real compares their names, and the queues real ships in every
/// database. Created on first use (<see cref="Database.ServiceBroker"/>), so a
/// database no statement asks about carries none of it.
/// </summary>
internal sealed class DatabaseBroker
{
    public readonly ConcurrentDictionary<string, BrokerMessageType> MessageTypes = new(StringComparer.Ordinal);
    public readonly ConcurrentDictionary<string, BrokerContract> Contracts = new(StringComparer.Ordinal);
    public readonly ConcurrentDictionary<string, BrokerService> Services = new(StringComparer.Ordinal);
    public readonly ConcurrentDictionary<string, BrokerRoute> Routes;
    public readonly ConcurrentDictionary<string, RemoteServiceBinding> RemoteServiceBindings;
    public readonly ConcurrentDictionary<string, BrokerPriority> Priorities;

    /// <summary>The queues real ships, which <c>sys.objects</c> doesn't list here (see the catalog divergences).</summary>
    public readonly ServiceQueue[] SystemQueues;

    private int nextMessageTypeId = 65535, nextContractId = 65535, nextServiceId = 65535, nextRouteId = 65536, nextBindingId = 65535, nextPriorityId = 65535;

    public int AllocateMessageTypeId() => Interlocked.Increment(ref this.nextMessageTypeId);
    public int AllocateContractId() => Interlocked.Increment(ref this.nextContractId);
    public int AllocateServiceId() => Interlocked.Increment(ref this.nextServiceId);
    public int AllocateRouteId() => Interlocked.Increment(ref this.nextRouteId);
    public int AllocateBindingId() => Interlocked.Increment(ref this.nextBindingId);
    public int AllocatePriorityId() => Interlocked.Increment(ref this.nextPriorityId);

    /// <summary>The last id each class handed out — message type, contract, service, route, binding, priority — as a backup carries them.</summary>
    public int[] CaptureIdCounters() => [this.nextMessageTypeId, this.nextContractId, this.nextServiceId, this.nextRouteId, this.nextBindingId, this.nextPriorityId];

    /// <summary>Puts back what <see cref="CaptureIdCounters"/> captured.</summary>
    public void RestoreIdCounters(int[] counters)
    {
        if (counters.Length != 6)
            return;
        (this.nextMessageTypeId, this.nextContractId, this.nextServiceId, this.nextRouteId, this.nextBindingId, this.nextPriorityId) =
            (counters[0], counters[1], counters[2], counters[3], counters[4], counters[5]);
    }

    public DatabaseBroker(Database database)
    {
        var names = database.Collation.NameKeys;
        this.Routes = new(names);
        this.RemoteServiceBindings = new(names);
        this.Priorities = new(names);

        var types = new BrokerMessageType[SystemMessageTypes.Length];
        for (var i = 0; i < types.Length; i++)
        {
            var (name, validation) = SystemMessageTypes[i];
            types[i] = new BrokerMessageType(i + 1, name, Database.DboPrincipalId, validation, null, isSystem: true);
            this.MessageTypes[name] = types[i];
        }
        var contracts = new BrokerContract[SystemContracts.Length];
        for (var i = 0; i < contracts.Length; i++)
        {
            var (name, usages) = SystemContracts[i];
            contracts[i] = new BrokerContract(i + 1, name, Database.DboPrincipalId,
                Array.ConvertAll(usages, usage => new ContractUsage(types[usage.Type - 1], usage.Initiator, usage.Target)), isSystem: true);
            this.Contracts[name] = contracts[i];
        }

        var master = database.Name.Equals(Simulation.MasterDatabaseName, StringComparison.OrdinalIgnoreCase);
        var dbo = database.Schemas[Database.DefaultSchemaName];
        var queues = new List<ServiceQueue>(6);
        ServiceQueue SystemQueue(string name, int objectId, DateTime created, QueueSettings settings)
        {
            var queue = new ServiceQueue(dbo, name, objectId, created, settings) { IsMsShipped = true, InternalTableId = objectId + 16_000_057 };
            queues.Add(queue);
            return queue;
        }
        var plain = new QueueSettings { Enabled = true };
        var queryNotifications = SystemQueue("QueryNotificationErrorsQueue", master ? 1003150619 : 1977058079, master ? new(2009, 4, 13, 12, 59, 8, 967) : new(2009, 4, 13, 12, 59, 13, 483), plain);
        var eventNotifications = SystemQueue("EventNotificationErrorsQueue", master ? 1035150733 : 2009058193, master ? new(2009, 4, 13, 12, 59, 8, 983) : new(2009, 4, 13, 12, 59, 13, 500), plain);
        var serviceBroker = SystemQueue("ServiceBrokerQueue", master ? 1067150847 : 2041058307, master ? new(2009, 4, 13, 12, 59, 8, 983) : new(2009, 4, 13, 12, 59, 13, 500), plain);
        this.Services["http://schemas.microsoft.com/SQL/Notifications/QueryNotificationService"] =
            new BrokerService(1, "http://schemas.microsoft.com/SQL/Notifications/QueryNotificationService", Database.DboPrincipalId, queryNotifications, [], isSystem: true);
        this.Services["http://schemas.microsoft.com/SQL/Notifications/EventNotificationService"] =
            new BrokerService(2, "http://schemas.microsoft.com/SQL/Notifications/EventNotificationService", Database.DboPrincipalId, eventNotifications, [], isSystem: true);
        this.Services["http://schemas.microsoft.com/SQL/ServiceBroker/ServiceBroker"] =
            new BrokerService(3, "http://schemas.microsoft.com/SQL/ServiceBroker/ServiceBroker", Database.DboPrincipalId, serviceBroker, [contracts[3], contracts[4]], isSystem: true);
        this.Routes["AutoCreatedLocal"] = new BrokerRoute(65536, "AutoCreatedLocal", Database.DboPrincipalId) { Address = "LOCAL" };

        // msdb ships Database Mail's and policy-based management's services
        // (probed 2026-10-16 against SQL Server 2025).
        if (database.Name.Equals(Simulation.MsdbDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            var sendMail = new BrokerMessageType(this.AllocateMessageTypeId(), "{//www.microsoft.com/databasemail/messages}SendMail", Database.DboPrincipalId, MessageValidation.None, null, isSystem: true);
            var sendMailStatus = new BrokerMessageType(this.AllocateMessageTypeId(), "{//www.microsoft.com/databasemail/messages}SendMailStatus", Database.DboPrincipalId, MessageValidation.None, null, isSystem: true);
            this.MessageTypes[sendMail.Name] = sendMail;
            this.MessageTypes[sendMailStatus.Name] = sendMailStatus;
            var mail = new BrokerContract(this.AllocateContractId(), "//www.microsoft.com/databasemail/contracts/SendMail/v1.0", Database.DboPrincipalId,
                [new ContractUsage(sendMail, true, false), new ContractUsage(sendMailStatus, false, true)], isSystem: true);
            this.Contracts[mail.Name] = mail;
            static QueueSettings Activated(string procedure, int executeAs) => new()
            {
                Enabled = true,
                PoisonMessageHandling = true,
                ActivationEnabled = true,
                ActivationProcedure = $"[dbo].[{procedure}]",
                ActivationSchema = Database.DefaultSchemaName,
                ActivationName = procedure,
                MaxReaders = 1,
                ExecuteAsPrincipalId = executeAs,
            };
            var internalMail = SystemQueue("InternalMailQueue", 1003150619, new(2026, 7, 9, 0, 29, 40, 833), Activated("sp_ExternalMailQueueListener", Database.DboPrincipalId));
            var externalMail = SystemQueue("ExternalMailQueue", 1035150733, new(2026, 7, 9, 0, 29, 40, 840), Activated("sp_sysmail_activate", Database.DboPrincipalId));
            var policyEvents = SystemQueue("syspolicy_event_queue", 800721905, new(2026, 7, 9, 0, 29, 48, 757), Activated("sp_syspolicy_events_reader", 20));
            this.Services["InternalMailService"] = new BrokerService(this.AllocateServiceId(), "InternalMailService", Database.DboPrincipalId, internalMail, [mail], isSystem: true);
            this.Services["ExternalMailService"] = new BrokerService(this.AllocateServiceId(), "ExternalMailService", Database.DboPrincipalId, externalMail, [mail], isSystem: true);
            this.Services["syspolicy_event_listener"] = new BrokerService(this.AllocateServiceId(), "syspolicy_event_listener", Database.DboPrincipalId, policyEvents, [contracts[1]], isSystem: true);
        }
        this.SystemQueues = [.. queues];
    }

    /// <summary>The message types every database ships, in id order (probed 2026-10-16 against SQL Server 2025).</summary>
    private static readonly (string Name, MessageValidation Validation)[] SystemMessageTypes =
    [
        ("http://schemas.microsoft.com/SQL/ServiceBroker/Error", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog", MessageValidation.Empty),
        ("http://schemas.microsoft.com/SQL/Notifications/QueryNotification", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/Notifications/EventNotification", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/DialogTimer", MessageValidation.Empty),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice/MissingRoute", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice/FailedRoute", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice/MissingRemoteServiceBinding", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice/FailedRemoteServiceBinding", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/ServiceEcho/Echo", MessageValidation.None),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/ServiceDiagnostic/Query", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/ServiceDiagnostic/Status", MessageValidation.WellFormedXml),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/ServiceDiagnostic/Description", MessageValidation.None),
        ("DEFAULT", MessageValidation.None),
    ];

    /// <summary>The contracts every database ships, in id order, each usage naming a message type by id.</summary>
    private static readonly (string Name, (int Type, bool Initiator, bool Target)[] Usages)[] SystemContracts =
    [
        ("http://schemas.microsoft.com/SQL/Notifications/PostQueryNotification", [(3, true, false)]),
        ("http://schemas.microsoft.com/SQL/Notifications/PostEventNotification", [(4, true, false)]),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice", [(6, true, false), (7, true, false), (8, true, false), (9, true, false)]),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/ServiceEcho", [(10, true, true)]),
        ("http://schemas.microsoft.com/SQL/ServiceBroker/ServiceDiagnostic", [(11, true, false), (12, false, true), (13, false, true)]),
        ("DEFAULT", [(14, true, true)]),
    ];

    /// <summary>Error and EndDialog, the system messages a dialog sends whatever its contract.</summary>
    public BrokerMessageType ErrorType => this.MessageTypes["http://schemas.microsoft.com/SQL/ServiceBroker/Error"];

    public BrokerMessageType EndDialogType => this.MessageTypes["http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog"];

    public BrokerMessageType DialogTimerType => this.MessageTypes["http://schemas.microsoft.com/SQL/ServiceBroker/DialogTimer"];

    /// <summary>The <c>DEFAULT</c> contract, what a dialog without <c>ON CONTRACT</c> uses.</summary>
    public BrokerContract DefaultContract => this.Contracts["DEFAULT"];

    /// <summary>Whether <paramref name="name"/> is one of the queues real ships, in any database.</summary>
    public static bool IsSystemQueueName(string name) =>
        BuiltInToken.EqualsAny(name, "QueryNotificationErrorsQueue", "EventNotificationErrorsQueue", "ServiceBrokerQueue",
            "InternalMailQueue", "ExternalMailQueue", "syspolicy_event_queue");

    /// <summary>The queue named <paramref name="leaf"/> in <paramref name="schema"/>: a user one, or one real ships in <c>dbo</c>.</summary>
    public ServiceQueue? FindQueue(Schema schema, string leaf)
    {
        if (schema.Queues.TryGetValue(leaf, out var queue))
            return queue;
        if (schema.SchemaId != Database.DboSchemaId)
            return null;
        foreach (var system in this.SystemQueues)
        {
            if (schema.Database.Collation.Equals(system.Name, leaf))
                return system;
        }
        return null;
    }

    /// <summary>Every queue of the database, the shipped ones first.</summary>
    public IEnumerable<ServiceQueue> AllQueues(Database database)
    {
        foreach (var queue in this.SystemQueues)
            yield return queue;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, queue) in schema.Queues)
                yield return queue;
        }
    }

    /// <summary>The service whose queue is <paramref name="queue"/>, if any.</summary>
    public bool QueueHasService(ServiceQueue queue)
    {
        foreach (var (_, service) in this.Services)
        {
            if (ReferenceEquals(service.Queue, queue))
                return true;
        }
        return false;
    }
}
