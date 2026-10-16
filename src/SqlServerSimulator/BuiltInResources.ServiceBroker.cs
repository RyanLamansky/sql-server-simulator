using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static readonly NVarcharSqlType brokerName128 = NVarcharSqlType.Get(128, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);
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
            new("name", brokerName128, 128, false),
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
            new("name", brokerName128, 128, false),
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
            new("name", brokerName128, 128, false),
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
        ], static (_, _) => EmptyCatalogRows);

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
    /// <c>sys.dm_broker_queue_monitors</c>: one row per queue with an
    /// activation procedure in every database that has used Service Broker,
    /// <c>INACTIVE</c> while no activated reader runs.
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
                yield return [
                    SqlValue.FromInt32(database.Id),
                    SqlValue.FromInt32(queue.ObjectId),
                    SqlValue.FromNVarchar("INACTIVE"),
                    SqlValue.FromDateTime(queue.CreateDate),
                    SqlValue.FromDateTime(queue.CreateDate),
                    SqlValue.FromInt32(0),
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
    internal static CatalogView QueueRowset(ServiceQueue queue, Database database) =>
        new(queue.Name, QueueColumns, (batch, _) =>
        {
            var owner = batch.Connection.LockOwner;
            var rows = new List<SqlValue[]>();
            lock (database.ServiceBroker.Gate)
            {
                foreach (var message in queue.Messages)
                {
                    if (message.PendingOwner is null || ReferenceEquals(message.PendingOwner, owner))
                        rows.Add(QueueRow(message));
                }
            }
            return rows;
        })
        {
            ObjectId = queue.ObjectId,
        };
}
