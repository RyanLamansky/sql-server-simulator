using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static readonly NVarcharSqlType brokerName128 = NVarcharSqlType.Get(128, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);

    /// <summary>
    /// The <c>sysname</c> the services, contracts and message types are named
    /// by, in <c>Latin1_General_BIN</c> (probed 2026-10-18 against SQL Server
    /// 2025: each <c>name</c> describes as user type 256 in that collation).
    /// </summary>
    private static readonly SystemNameSqlType brokerSysname = SystemNameSqlType.Get(Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);
    private static readonly NVarcharSqlType brokerName256 = NVarcharSqlType.Get(256, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);

    /// <summary>
    /// Registers the Service Broker catalog views, each projecting the
    /// database's <see cref="DatabaseBroker"/> — the objects real ships in
    /// every database (fourteen message types, six contracts, three services
    /// and queues, the <c>AutoCreatedLocal</c> route; msdb adds Database
    /// Mail's and policy-based management's) and the ones statements create.
    /// Shapes probed 2026-07-16 and rows 2026-10-16 against SQL Server 2025;
    /// see docs/claude/service-broker.md.
    /// </summary>
    private static void RegisterServiceBroker(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        Sys("services",
        [
            new("name", brokerSysname, 128, false),
            new("service_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("service_queue_id", SqlType.Int32, null, false),
        ], static (_, database) => BrokerServices(database).Select(service => (SqlValue[])[
            SqlValue.FromNVarchar(service.Name),
            SqlValue.FromInt32(service.Id),
            SqlValue.FromInt32(service.PrincipalId),
            SqlValue.FromInt32(service.Queue.ObjectId),
        ]));
        Sys("service_queues",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("is_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
            new("max_readers", SqlType.SmallInt, null, true),
            new("activation_procedure", SqlType.NVarchar, 776, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
            new("is_activation_enabled", SqlType.Bit, null, false),
            new("is_receive_enabled", SqlType.Bit, null, false),
            new("is_enqueue_enabled", SqlType.Bit, null, false),
            new("is_retention_enabled", SqlType.Bit, null, false),
            new("is_poison_message_handling_enabled", SqlType.Bit, null, true),
        ], static (_, database) => EnumerateServiceQueues(database));
        Sys("service_contracts",
        [
            new("name", brokerSysname, 128, false),
            new("service_contract_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
        ], static (_, database) => BrokerContracts(database).Select(contract => (SqlValue[])[
            SqlValue.FromNVarchar(contract.Name),
            SqlValue.FromInt32(contract.Id),
            SqlValue.FromInt32(contract.PrincipalId),
        ]));
        Sys("service_contract_usages",
        [
            new("service_id", SqlType.Int32, null, false),
            new("service_contract_id", SqlType.Int32, null, false),
        ], static (_, database) => BrokerServices(database).OrderBy(static service => service.Id)
            .SelectMany(service => service.Contracts.OrderBy(static contract => contract.Id).Select(contract => (SqlValue[])[
                SqlValue.FromInt32(service.Id),
                SqlValue.FromInt32(contract.Id),
            ])));
        Sys("service_queue_usages",
        [
            new("service_id", SqlType.Int32, null, false),
            new("service_queue_id", SqlType.Int32, null, false),
        ], static (_, database) => BrokerServices(database).OrderBy(static service => service.Id).Select(service => (SqlValue[])[
            SqlValue.FromInt32(service.Id),
            SqlValue.FromInt32(service.Queue.ObjectId),
        ]));
        Sys("service_contract_message_usages",
        [
            new("service_contract_id", SqlType.Int32, null, false),
            new("message_type_id", SqlType.Int32, null, false),
            new("is_sent_by_initiator", SqlType.Bit, null, false),
            new("is_sent_by_target", SqlType.Bit, null, false),
        ], static (_, database) => BrokerContracts(database).OrderBy(static contract => contract.Id)
            .SelectMany(contract => contract.Usages.OrderBy(static usage => usage.MessageType.Id).Select(usage => (SqlValue[])[
                SqlValue.FromInt32(contract.Id),
                SqlValue.FromInt32(usage.MessageType.Id),
                SqlValue.FromBoolean(usage.SentByInitiator),
                SqlValue.FromBoolean(usage.SentByTarget),
            ])));
        Sys("message_type_xml_schema_collection_usages",
        [
            new("message_type_id", SqlType.Int32, null, false),
            new("xml_collection_id", SqlType.Int32, null, false),
        ], static (_, database) => BrokerMessageTypes(database).Where(static type => type.Collection is not null).Select(type => (SqlValue[])[
            SqlValue.FromInt32(type.Id),
            SqlValue.FromInt32(type.Collection!.Id),
        ]));
        var validationChar = CharSqlType.Get(2, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);
        Sys("service_message_types",
        [
            new("name", brokerSysname, 128, false),
            new("message_type_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("validation", validationChar, 2, false),
            new("validation_desc", nvarchar60Catalog, 60, true),
            new("xml_collection_id", SqlType.Int32, null, true),
        ], (_, database) => BrokerMessageTypes(database).Select(type => (SqlValue[])[
            SqlValue.FromNVarchar(type.Name),
            SqlValue.FromInt32(type.Id),
            SqlValue.FromInt32(type.PrincipalId),
            SqlValue.FromChar(validationChar, type.ValidationCode.ToString()),
            SqlValue.FromNVarchar(type.Validation switch
            {
                MessageValidation.Empty => "EMPTY",
                MessageValidation.None => "BINARY",
                _ => "XML",
            }),
            type.Collection is { } collection ? SqlValue.FromInt32(collection.Id) : SqlValue.Null(SqlType.Int32),
        ]));
        Sys("routes",
        [
            new("name", SqlType.SystemName, 128, false),
            new("route_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("remote_service_name", brokerName256, 256, true),
            new("broker_instance", brokerName128, 128, true),
            new("lifetime", SqlType.DateTime, null, true),
            new("address", brokerName256, 256, true),
            new("mirror_address", brokerName256, 256, true),
        ], static (_, database) => database.ServiceBroker.Routes.EnumerateValues().OrderBy(static route => route.Id).Select(route => (SqlValue[])[
            SqlValue.FromSystemName(route.Name),
            SqlValue.FromInt32(route.Id),
            SqlValue.FromInt32(route.PrincipalId),
            NVarcharOrNull(route.RemoteServiceName),
            NVarcharOrNull(route.BrokerInstance),
            route.Lifetime is { } lifetime ? SqlValue.FromDateTime(lifetime) : SqlValue.Null(SqlType.DateTime),
            SqlValue.FromNVarchar(route.Address),
            NVarcharOrNull(route.MirrorAddress),
        ]));
        Sys("conversation_priorities",
        [
            new("priority_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("service_contract_id", SqlType.Int32, null, true),
            new("local_service_id", SqlType.Int32, null, true),
            new("remote_service_name", SqlType.NVarchar, 256, true),
            new("priority", SqlType.TinyInt, null, false),
        ], static (_, database) => database.ServiceBroker.Priorities.EnumerateValues().OrderBy(static priority => priority.Id).Select(priority => (SqlValue[])[
            SqlValue.FromInt32(priority.Id),
            SqlValue.FromSystemName(priority.Name),
            priority.ContractId is { } contract ? SqlValue.FromInt32(contract) : SqlValue.Null(SqlType.Int32),
            priority.LocalServiceId is { } local ? SqlValue.FromInt32(local) : SqlValue.Null(SqlType.Int32),
            NVarcharOrNull(priority.RemoteServiceName),
            SqlValue.FromByte(priority.Level),
        ]));
        Sys("remote_service_bindings",
        [
            new("name", SqlType.SystemName, 128, false),
            new("remote_service_binding_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("remote_service_name", brokerName256, 256, true),
            new("service_contract_id", SqlType.Int32, null, false),
            new("remote_principal_id", SqlType.Int32, null, true),
            new("is_anonymous_on", SqlType.Bit, null, false),
        ], static (_, database) => database.ServiceBroker.RemoteServiceBindings.EnumerateValues().OrderBy(static binding => binding.Id).Select(binding => (SqlValue[])[
            SqlValue.FromSystemName(binding.Name),
            SqlValue.FromInt32(binding.Id),
            SqlValue.FromInt32(binding.PrincipalId),
            SqlValue.FromNVarchar(binding.RemoteServiceName),
            SqlValue.FromInt32(0),
            SqlValue.FromInt32(binding.RemotePrincipalId),
            SqlValue.FromBoolean(binding.Anonymous),
        ]));
        Sys("event_notifications",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("parent_class", SqlType.TinyInt, null, false),
            new("parent_class_desc", nvarchar60Catalog, 60, true),
            new("parent_id", SqlType.Int32, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("service_name", brokerName256, 256, true),
            new("broker_instance", brokerName128, 128, true),
            new("creator_sid", SqlType.Varbinary, 85, true),
            new("principal_id", SqlType.Int32, null, true),
        ], static (batch, database) => database.EventNotifications.Where(notification => SeesEventNotification(batch, database, notification))
            .OrderBy(static notification => notification.ObjectId).Select(EventNotificationRow));
        Sys("server_event_notifications",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("parent_class", SqlType.TinyInt, null, false),
            new("parent_class_desc", nvarchar60Catalog, 60, true),
            new("parent_id", SqlType.Int32, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("service_name", brokerName256, 256, true),
            new("broker_instance", brokerName128, 128, true),
            new("creator_sid", SqlType.Varbinary, 85, true),
            new("principal_id", SqlType.Int32, null, true),
        ], static (batch, _) => batch.Connection.Simulation.ServerEventNotifications.Where(notification => SeesServerEventNotification(batch, notification))
            .OrderBy(static notification => notification.ObjectId).Select(EventNotificationRow));
        // In the database's collation, as CatalogView.ColumnsIn reads it (probed
        // 2026-10-18 against SQL Server 2025).
        var eventTypeName = NVarcharSqlType.Get(64, Collation.Baseline, Coercibility.Implicit);
        Sys("event_notification_event_types",
        [
            new("type", SqlType.Int32, null, false),
            new("type_name", eventTypeName, 64, true),
            new("parent_type", SqlType.Int32, null, true),
        ], (_, _) => EventNotificationEventTypes.All.Select(entry => (SqlValue[])[
            SqlValue.FromInt32(entry.Type),
            SqlValue.FromString(eventTypeName, entry.TypeName),
            entry.ParentType is int parent ? SqlValue.FromInt32(parent) : SqlValue.Null(SqlType.Int32),
        ]));

        var stateChar = CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit);
        Sys("conversation_endpoints",
        [
            new("conversation_handle", SqlType.UniqueIdentifier, null, false),
            new("conversation_id", SqlType.UniqueIdentifier, null, false),
            new("is_initiator", SqlType.Bit, null, false),
            new("service_contract_id", SqlType.Int32, null, false),
            new("conversation_group_id", SqlType.UniqueIdentifier, null, false),
            new("service_id", SqlType.Int32, null, false),
            new("lifetime", SqlType.DateTime, null, false),
            new("state", stateChar, 2, false),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("far_service", brokerName256, 256, false),
            new("far_broker_instance", brokerName128, 128, true),
            new("principal_id", SqlType.Int32, null, false),
            new("far_principal_id", SqlType.Int32, null, false),
            new("outbound_session_key_identifier", SqlType.UniqueIdentifier, null, false),
            new("inbound_session_key_identifier", SqlType.UniqueIdentifier, null, false),
            new("security_timestamp", SqlType.DateTime, null, false),
            new("dialog_timer", SqlType.DateTime, null, false),
            new("send_sequence", SqlType.BigInt, null, false),
            new("last_send_tran_id", SqlType.GetBinary(6), 6, false),
            new("end_dialog_sequence", SqlType.BigInt, null, false),
            new("receive_sequence", SqlType.BigInt, null, false),
            new("receive_sequence_frag", SqlType.Int32, null, false),
            new("system_sequence", SqlType.BigInt, null, false),
            new("first_out_of_order_sequence", SqlType.BigInt, null, false),
            new("last_out_of_order_sequence", SqlType.BigInt, null, false),
            new("last_out_of_order_frag", SqlType.Int32, null, false),
            new("is_system", SqlType.Bit, null, false),
            new("priority", SqlType.TinyInt, null, false),
        ], (batch, database) => EnumerateConversationEndpoints(batch, database, stateChar));
        Sys("conversation_groups",
        [
            new("conversation_group_id", SqlType.UniqueIdentifier, null, false),
            new("service_id", SqlType.Int32, null, false),
            new("is_system", SqlType.Bit, null, true),
        ], static (batch, database) => EnumerateConversationGroups(batch, database));
        Sys("transmission_queue",
        [
            new("conversation_handle", SqlType.UniqueIdentifier, null, false),
            new("to_service_name", brokerName256, 256, true),
            new("to_broker_instance", brokerName128, 128, true),
            new("from_service_name", brokerName256, 256, true),
            new("service_contract_name", brokerName256, 256, true),
            new("enqueue_time", SqlType.DateTime, null, false),
            new("message_sequence_number", SqlType.BigInt, null, false),
            new("message_type_name", brokerName256, 256, true),
            new("is_conversation_error", SqlType.Bit, null, false),
            new("is_end_of_dialog", SqlType.Bit, null, false),
            new("message_body", SqlType.VarbinaryMax, SqlType.MaxLengthSentinel, true),
            new("transmission_status", SqlType.NVarchar, 4000, true),
            new("priority", SqlType.TinyInt, null, false),
        ], static (batch, database) => EnumerateTransmissionQueue(batch, database));

        Sys("dm_qn_subscriptions",
        [
            new("id", SqlType.Int32, null, false),
            new("database_id", SqlType.Int32, null, false),
            new("sid", SqlType.Varbinary, 85, false),
            new("object_id", SqlType.Int32, null, false),
            new("created", SqlType.DateTime, null, false),
            new("timeout", SqlType.Int32, null, false),
            new("status", SqlType.Int32, null, false),
        ], static (batch, _) => QuerySubscriptionRows(batch.Connection.Simulation));

        var defaultName = NVarcharSqlType.Get(32, Collation.Baseline, Coercibility.Implicit);
        Sys("dm_broker_queue_monitors",
        [
            new("database_id", SqlType.Int32, null, true),
            new("queue_id", SqlType.Int32, null, true),
            new("state", defaultName, 32, true),
            new("last_empty_rowset_time", SqlType.DateTime, null, true),
            new("last_activated_time", SqlType.DateTime, null, true),
            new("tasks_waiting", SqlType.Int32, null, true),
        ], static (batch, _) => EnumerateQueueMonitors(batch));
    }

    /// <summary>
    /// <c>sys.conversation_endpoints</c>: the database's endpoints as the
    /// reading session sees them, in handle order. The session keys, security
    /// timestamp and out-of-order bookkeeping read as a local dialog's; an
    /// unset timer as 1900-01-01.
    /// </summary>
    private static List<SqlValue[]> EnumerateConversationEndpoints(Parser.BatchContext batch, Database database, CharSqlType stateChar)
    {
        var rows = new List<SqlValue[]>();
        if (batch.Connection.Simulation.BrokerIfUsed is not { } broker)
            return rows;
        var owner = batch.Connection.LockOwner;
        var never = SqlValue.FromDateTime(new DateTime(1900, 1, 1));
        var noKey = SqlValue.FromGuid(Guid.Empty);
        var zeroLong = SqlValue.FromInt64(0);
        var zero = SqlValue.FromInt32(0);
        var noTransaction = SqlValue.FromBinary(SqlType.GetBinary(6), new byte[6]);
        lock (broker.Gate)
        {
            broker.FireDue(DateTime.UtcNow);
            foreach (var (_, endpoint) in broker.Endpoints.OrderBy(static pair => pair.Value.Handle))
            {
                if (!ReferenceEquals(endpoint.Database, database) || !BrokerRuntime.Sees(endpoint.PendingOwner, owner))
                    continue;
                rows.Add([
                    SqlValue.FromGuid(endpoint.Handle),
                    SqlValue.FromGuid(endpoint.ConversationId),
                    SqlValue.FromBoolean(endpoint.IsInitiator),
                    SqlValue.FromInt32(endpoint.Contract.Id),
                    SqlValue.FromGuid(endpoint.Group.Id),
                    SqlValue.FromInt32(endpoint.Service.Id),
                    SqlValue.FromDateTime(endpoint.Lifetime),
                    SqlValue.FromChar(stateChar, endpoint.StateCode),
                    SqlValue.FromNVarchar(endpoint.StateDescription),
                    SqlValue.FromNVarchar(endpoint.FarService),
                    NVarcharOrNull(endpoint.FarBrokerInstance),
                    SqlValue.FromInt32(endpoint.PrincipalId),
                    SqlValue.FromInt32(endpoint.FarPrincipalId),
                    noKey,
                    noKey,
                    never,
                    endpoint.DialogTimer is { } timer ? SqlValue.FromDateTime(timer) : never,
                    SqlValue.FromInt64(endpoint.SendSequence),
                    noTransaction,
                    SqlValue.FromInt64(endpoint.EndDialogSequence),
                    SqlValue.FromInt64(endpoint.ReceiveSequence),
                    zero,
                    zeroLong,
                    SqlValue.FromInt64(-1),
                    zeroLong,
                    zero,
                    notMsShipped,
                    SqlValue.FromByte(5),
                ]);
            }
        }
        return rows;
    }

    /// <summary><c>sys.conversation_groups</c>: the database's conversation groups as the reading session sees them.</summary>
    private static List<SqlValue[]> EnumerateConversationGroups(Parser.BatchContext batch, Database database)
    {
        var rows = new List<SqlValue[]>();
        if (batch.Connection.Simulation.BrokerIfUsed is not { } broker)
            return rows;
        var owner = batch.Connection.LockOwner;
        lock (broker.Gate)
        {
            foreach (var (_, group) in broker.Groups.OrderBy(static pair => pair.Value.Id))
            {
                if (ReferenceEquals(group.Database, database) && BrokerRuntime.Sees(group.PendingOwner, owner))
                    rows.Add([SqlValue.FromGuid(group.Id), SqlValue.FromInt32(group.Service.Id), notMsShipped]);
            }
        }
        return rows;
    }

    /// <summary><c>sys.transmission_queue</c>: the messages the database's endpoints sent that wait undelivered.</summary>
    private static List<SqlValue[]> EnumerateTransmissionQueue(Parser.BatchContext batch, Database database)
    {
        var rows = new List<SqlValue[]>();
        if (batch.Connection.Simulation.BrokerIfUsed is not { } broker)
            return rows;
        var owner = batch.Connection.LockOwner;
        lock (broker.Gate)
        {
            foreach (var message in broker.Transmission)
            {
                var from = message.From;
                if (!ReferenceEquals(from.Database, database) || !BrokerRuntime.Sees(message.PendingOwner, owner))
                    continue;
                rows.Add([
                    SqlValue.FromGuid(from.Handle),
                    SqlValue.FromNVarchar(from.FarService),
                    SqlValue.FromNVarchar(from.FarBrokerInstance ?? ""),
                    SqlValue.FromNVarchar(from.Service.Name),
                    SqlValue.FromNVarchar(from.Contract.Name),
                    SqlValue.FromDateTime(message.EnqueueTime),
                    SqlValue.FromInt64(message.Sequence),
                    SqlValue.FromNVarchar(message.MessageTypeName),
                    SqlValue.FromBoolean(message.IsError),
                    SqlValue.FromBoolean(message.IsEndOfDialog),
                    message.Body is { } body ? SqlValue.FromVarbinary(SqlType.VarbinaryMax, body) : SqlValue.Null(SqlType.VarbinaryMax),
                    SqlValue.FromNVarchar(message.Status),
                    SqlValue.FromByte(5),
                ]);
            }
        }
        return rows;
    }

    private static SqlValue NVarcharOrNull(string? value) => value is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(value);

    /// <summary>The message types in id order.</summary>
    private static IEnumerable<BrokerMessageType> BrokerMessageTypes(Database database) =>
        database.ServiceBroker.MessageTypes.EnumerateValues().OrderBy(static type => type.Id);

    /// <summary>
    /// The contracts as real lists them: its own in name order, the ones
    /// statements create after them in id order.
    /// </summary>
    private static IEnumerable<BrokerContract> BrokerContracts(Database database) =>
        database.ServiceBroker.Contracts.EnumerateValues().OrderBy(static contract => !contract.IsSystem).ThenBy(static contract => contract.IsSystem ? contract.Name : "", StringComparer.Ordinal).ThenBy(static contract => contract.Id);

    /// <summary>The services as real lists them, ordered as <see cref="BrokerContracts"/> are.</summary>
    private static IEnumerable<BrokerService> BrokerServices(Database database) =>
        database.ServiceBroker.Services.EnumerateValues().OrderBy(static service => !service.IsSystem).ThenBy(static service => service.IsSystem ? service.Name : "", StringComparer.Ordinal).ThenBy(static service => service.Id);

    private static IEnumerable<SqlValue[]> EnumerateServiceQueues(Database database)
    {
        var typeCode = SqlValue.FromChar(charTwo, "SQ");
        var typeDesc = SqlValue.FromNVarchar("SERVICE_QUEUE");
        var zero = SqlValue.FromInt32(0);
        var broker = database.ServiceBroker;
        foreach (var queue in broker.SystemQueues.Concat(broker.AllQueues(database).Skip(broker.SystemQueues.Length).OrderBy(static queue => queue.ObjectId)))
        {
            var settings = queue.Settings;
            yield return [
                SqlValue.FromSystemName(queue.Name),
                SqlValue.FromInt32(queue.ObjectId),
                Ownership.PrincipalIdValue(queue.OwnerPrincipalId),
                SqlValue.FromInt32(queue.SchemaId),
                zero,
                typeCode,
                typeDesc,
                SqlValue.FromDateTime(queue.CreateDate),
                SqlValue.FromDateTime(queue.ModifyDate),
                SqlValue.FromBoolean(queue.IsMsShipped),
                notMsShipped,
                notMsShipped,
                SqlValue.FromInt16(settings.MaxReaders),
                NVarcharOrNull(settings.ActivationProcedure),
                settings.ExecuteAsPrincipalId is { } executeAs ? SqlValue.FromInt32(executeAs) : SqlValue.Null(SqlType.Int32),
                SqlValue.FromBoolean(settings.ActivationEnabled),
                SqlValue.FromBoolean(settings.Enabled),
                SqlValue.FromBoolean(settings.Enabled),
                SqlValue.FromBoolean(settings.Retention),
                SqlValue.FromBoolean(settings.PoisonMessageHandling),
            ];
        }
    }

    /// <summary>
    /// <c>sys.dm_qn_subscriptions</c>: one row per live query notification
    /// subscription, naming the first table it watches; <c>status</c> is the
    /// constant real reports for a live one (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    private static List<SqlValue[]> QuerySubscriptionRows(Simulation simulation)
    {
        var rows = new List<SqlValue[]>();
        lock (simulation.QuerySubscriptions)
        {
            foreach (var subscription in simulation.QuerySubscriptions)
            {
                rows.Add([
                    SqlValue.FromInt32(subscription.Id),
                    SqlValue.FromInt32(subscription.Database.Id),
                    SqlValue.FromVarbinary(subscription.Sid),
                    SqlValue.FromInt32(subscription.Tables[0].ObjectId),
                    SqlValue.FromDateTime(subscription.Created),
                    SqlValue.FromInt32(subscription.Request.Timeout),
                    SqlValue.FromInt32(199168),
                ]);
            }
        }
        return rows;
    }

    /// <summary><c>sys.dm_broker_activated_tasks</c>: each activation procedure running.</summary>
    private static List<SqlValue[]> ActivatedTaskRows(Simulation simulation)
    {
        var rows = new List<SqlValue[]>();
        lock (simulation.ActivatedTasks)
        {
            foreach (var task in simulation.ActivatedTasks)
            {
                rows.Add([
                    SqlValue.FromInt32(task.Spid),
                    SqlValue.FromInt16(task.Database.Id),
                    SqlValue.FromInt32(task.Queue.ObjectId),
                    SqlValue.FromNVarchar(task.Procedure),
                    SqlValue.FromInt32(task.ExecuteAs),
                ]);
            }
        }
        return rows;
    }

    /// <summary>
    /// <c>sys.dm_broker_queue_monitors</c>: one row per queue with an
    /// activation procedure in every database that has used Service Broker —
    /// <c>RECEIVES_OCCURRING</c> while a reader runs, <c>NOTIFIED</c> once one
    /// ended receiving nothing, else <c>INACTIVE</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateQueueMonitors(Parser.BatchContext batch)
    {
        foreach (var (_, database) in batch.Connection.Simulation.Databases)
        {
            // msdb's queues are always watched, Database Mail's among them.
            var msdb = database.Name.Equals(Simulation.MsdbDatabaseName, StringComparison.OrdinalIgnoreCase);
            if ((msdb ? database.ServiceBroker : database.ServiceBrokerIfUsed) is not { } broker)
                continue;
            foreach (var queue in broker.AllQueues(database))
            {
                if (queue.Settings.ActivationProcedure is null)
                    continue;
                var (state, waiting) = batch.Connection.Simulation.QueueMonitorState(queue);
                yield return [
                    SqlValue.FromInt32(database.Id),
                    SqlValue.FromInt32(queue.ObjectId),
                    SqlValue.FromNVarchar(state),
                    SqlValue.FromDateTime(queue.CreateDate),
                    SqlValue.FromDateTime(queue.CreateDate),
                    SqlValue.FromInt32(waiting),
                ];
            }
        }
    }
    private static readonly NCharSqlType validationNChar = NCharSqlType.Get(1, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);

    /// <summary>
    /// The columns a <c>SELECT</c> of a queue and a <c>RECEIVE</c> from one
    /// read (probed 2026-10-16 against SQL Server 2025), in that order.
    /// </summary>
    internal static readonly HeapColumn[] QueueColumns =
    [
        new("status", SqlType.TinyInt, null, false),
        new("priority", SqlType.TinyInt, null, true),
        new("queuing_order", SqlType.BigInt, null, false),
        new("conversation_group_id", SqlType.UniqueIdentifier, null, false),
        new("conversation_handle", SqlType.UniqueIdentifier, null, false),
        new("message_sequence_number", SqlType.BigInt, null, false),
        new("service_name", brokerName128, 128, false),
        new("service_id", SqlType.Int32, null, false),
        new("service_contract_name", brokerName128, 128, false),
        new("service_contract_id", SqlType.Int32, null, false),
        new("message_type_name", brokerName128, 128, false),
        new("message_type_id", SqlType.Int32, null, false),
        new("validation", validationNChar, 1, false),
        new("message_body", SqlType.VarbinaryMax, SqlType.MaxLengthSentinel, true),
        new("message_enqueue_time", SqlType.DateTime, null, true),
    ];

    /// <summary>One message as a row of <see cref="QueueColumns"/>.</summary>
    internal static SqlValue[] QueueRow(QueuedMessage message) =>
    [
        SqlValue.FromByte(message.Status),
        SqlValue.FromByte(message.Priority),
        SqlValue.FromInt64(message.QueuingOrder),
        SqlValue.FromGuid(message.ConversationGroupId),
        SqlValue.FromGuid(message.ConversationHandle),
        SqlValue.FromInt64(message.SequenceNumber),
        SqlValue.FromNVarchar(message.ServiceName),
        SqlValue.FromInt32(message.ServiceId),
        SqlValue.FromNVarchar(message.ContractName),
        SqlValue.FromInt32(message.ContractId),
        SqlValue.FromNVarchar(message.MessageTypeName),
        SqlValue.FromInt32(message.MessageTypeId),
        SqlValue.FromNChar(validationNChar, message.Validation.ToString()),
        message.Body is { } body ? SqlValue.FromVarbinary(SqlType.VarbinaryMax, body) : SqlValue.Null(SqlType.VarbinaryMax),
        SqlValue.FromDateTime(message.EnqueueTime),
    ];

    /// <summary>
    /// The rowset a <c>SELECT</c> of <paramref name="queue"/> reads: its
    /// committed messages and the reading session's own uncommitted ones, in
    /// <c>queuing_order</c>. Another transaction's uncommitted sends are
    /// passed over and its uncommitted receipts still listed, where real's
    /// read would wait on them.
    /// </summary>
    internal static CatalogView QueueRowset(ServiceQueue queue) =>
        new(queue.Name, QueueColumns, (batch, _) =>
        {
            var owner = batch.Connection.LockOwner;
            var rows = new List<SqlValue[]>();
            if (batch.Connection.Simulation.BrokerIfUsed is not { } broker)
                return rows;
            var listed = new List<QueuedMessage>();
            lock (broker.Gate)
            {
                broker.FireDue(DateTime.UtcNow);
                foreach (var message in queue.Messages)
                {
                    // A session's own RECEIVE takes the message from its view;
                    // another's still lists it until that commits.
                    if (BrokerRuntime.Sees(message.PendingOwner, owner) && !ReferenceEquals(message.ReceivedBy, owner))
                        listed.Add(message);
                }
            }
            // In the order of the queue's clustered index — status, group,
            // priority, conversation, then queuing order — which a scan reads.
            listed.Sort(static (a, b) =>
                a.Status != b.Status ? a.Status.CompareTo(b.Status)
                : new System.Data.SqlTypes.SqlGuid(a.ConversationGroupId).CompareTo(new System.Data.SqlTypes.SqlGuid(b.ConversationGroupId)) is var byGroup and not 0 ? byGroup
                : a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority)
                : new System.Data.SqlTypes.SqlGuid(a.ConversationHandle).CompareTo(new System.Data.SqlTypes.SqlGuid(b.ConversationHandle)) is var byHandle and not 0 ? byHandle
                : a.QueuingOrder.CompareTo(b.QueuingOrder));
            foreach (var message in listed)
                rows.Add(QueueRow(message));
            return rows;
        })
        {
            ObjectId = queue.ObjectId,
        };
    /// <summary>A row of <c>sys.event_notifications</c> or <c>sys.server_event_notifications</c>.</summary>
    private static SqlValue[] EventNotificationRow(EventNotification notification) =>
    [
        SqlValue.FromSystemName(notification.Name),
        SqlValue.FromInt32(notification.ObjectId),
        SqlValue.FromByte((byte)notification.Parent),
        SqlValue.FromString(nvarchar60Catalog, notification.Parent switch
        {
            EventNotificationParent.Database => "DATABASE",
            EventNotificationParent.Queue => "OBJECT_OR_COLUMN",
            _ => "SERVER",
        }),
        SqlValue.FromInt32(notification.Queue?.ObjectId ?? 0),
        SqlValue.FromDateTime(notification.CreateDate),
        SqlValue.FromDateTime(notification.CreateDate),
        SqlValue.FromString(brokerName256, notification.ServiceName),
        SqlValue.FromString(brokerName128, notification.BrokerInstance),
        notification.CreatorSid is { } sid ? SqlValue.FromVarbinary(VarbinarySqlType.Get(85), sid) : SqlValue.Null(VarbinarySqlType.Get(85)),
        SqlValue.FromInt32(notification.PrincipalId),
    ];

    /// <summary>
    /// Whether the reading principal sees a database's or queue's event
    /// notification: its owner, the holder of <c>ALTER ANY DATABASE EVENT
    /// NOTIFICATION</c>, or for a queue's whoever may alter the queue (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static bool SeesEventNotification(Parser.BatchContext batch, Database database, EventNotification notification) =>
        PermissionEnforcement.MayActAs(batch, database, notification.PrincipalId)
        || PermissionEnforcement.HoldsDatabasePermission(batch, database, "ALTER ANY DATABASE EVENT NOTIFICATION")
        || (notification.Queue is { } queue && PermissionEnforcement.HasObjectAlter(batch, database, queue.ObjectId, queue.SchemaId));

    /// <summary>Whether the reading login sees a server event notification: its own, or every one under <c>ALTER ANY EVENT NOTIFICATION</c>.</summary>
    private static bool SeesServerEventNotification(Parser.BatchContext batch, EventNotification notification)
    {
        var connection = batch.Connection;
        var simulation = connection.Simulation;
        return simulation.SessionHoldsServerPermission(connection, Permission.AlterAnyEventNotification)
            || (simulation.TryResolveServerPrincipalId(connection.Security.Effective.LoginName, out var principalId) && principalId == notification.PrincipalId);
    }

    /// <summary>
    /// The event rows of <c>sys.events</c> / <c>sys.server_events</c> one
    /// event notification contributes: each leaf event it takes, the group it
    /// was named through, <c>is_trigger_event</c> 0.
    /// </summary>
    internal static IEnumerable<SqlValue[]> EventNotificationEventRows(EventNotification notification, NVarcharSqlType typeName)
    {
        var objectId = SqlValue.FromInt32(notification.ObjectId);
        for (var i = 0; i < notification.Events.Length; i++)
        {
            var entry = notification.Events[i];
            var (groupType, groupName) = notification.Groups[i] is int named && EventNotificationEventTypes.TryResolveType(named, out var group)
                ? (SqlValue.FromInt32(group.Type), SqlValue.FromString(typeName, group.TypeName))
                : (SqlValue.Null(SqlType.Int32), SqlValue.Null(typeName));
            yield return [
                objectId,
                SqlValue.FromInt32(entry.Type),
                SqlValue.FromString(typeName, entry.TypeName),
                SqlValue.FromBoolean(false),
                groupType,
                groupName,
            ];
        }
    }
}
