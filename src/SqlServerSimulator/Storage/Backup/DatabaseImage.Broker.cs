using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage.Backup;

// The Service Broker state a backup carries: the database's broker id, its
// message types, contracts, queues (settings and messages), services, routes,
// remote service bindings and priorities under their own ids, its
// conversations — endpoints, groups and the messages its transmission queue
// holds — and its event notifications. None of it is recreated through DDL: a conversation has no statement
// that would rebuild it, so the whole state travels as one value, written
// from the live objects and read straight into the restored database's.
partial class DatabaseImage
{
    public Guid ServiceBrokerGuid;

    /// <summary>The broker state as <see cref="CaptureBroker"/> wrote it; null for a database that never used its broker.</summary>
    public byte[]? BrokerState;

    private void CaptureBroker(Database database, BrokerRuntime? conversations)
    {
        this.ServiceBrokerGuid = database.ServiceBrokerGuid;
        if (database.ServiceBrokerIfUsed is not { } objects)
            return;
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            if (conversations is null)
            {
                WriteBrokerObjects(w, database, objects);
                WriteConversations(w, database, null);
                WriteEventNotifications(w, database);
            }
            else
            {
                // The queues' messages and the conversations share the runtime's gate.
                lock (conversations.Gate)
                {
                    WriteBrokerObjects(w, database, objects);
                    WriteConversations(w, database, conversations);
                    WriteEventNotifications(w, database);
                }
            }
        }
        this.BrokerState = buffer.ToArray();
    }

    private static void WriteBrokerObjects(BinaryWriter w, Database database, DatabaseBroker objects)
    {
        BackupBinary.WriteInts(w, objects.CaptureIdCounters());
        var types = objects.MessageTypes.Select(static pair => pair.Value).Where(static type => !type.IsSystem).OrderBy(static type => type.Id).ToList();
        w.Write(types.Count);
        foreach (var type in types)
        {
            w.Write(type.Id);
            w.Write(type.Name);
            w.Write(type.PrincipalId);
            w.Write((byte)type.Validation);
            BackupBinary.WriteNullableInt(w, type.Collection?.Id);
        }
        var contracts = objects.Contracts.Select(static pair => pair.Value).Where(static contract => !contract.IsSystem).OrderBy(static contract => contract.Id).ToList();
        w.Write(contracts.Count);
        foreach (var contract in contracts)
        {
            w.Write(contract.Id);
            w.Write(contract.Name);
            w.Write(contract.PrincipalId);
            w.Write(contract.Usages.Length);
            foreach (var usage in contract.Usages)
            {
                w.Write(usage.MessageType.Name);
                w.Write(usage.SentByInitiator);
                w.Write(usage.SentByTarget);
            }
        }
        var queues = objects.AllQueues(database).ToList();
        w.Write(queues.Count);
        foreach (var queue in queues)
            WriteQueue(w, queue);
        var services = objects.Services.Select(static pair => pair.Value).Where(static service => !service.IsSystem).OrderBy(static service => service.Id).ToList();
        w.Write(services.Count);
        foreach (var service in services)
        {
            w.Write(service.Id);
            w.Write(service.Name);
            w.Write(service.PrincipalId);
            w.Write(service.Queue.Schema.Name);
            w.Write(service.Queue.Name);
            w.Write(service.Contracts.Length);
            foreach (var contract in service.Contracts)
                w.Write(contract.Name);
        }
        var routes = objects.Routes.Select(static pair => pair.Value).OrderBy(static route => route.Id).ToList();
        w.Write(routes.Count);
        foreach (var route in routes)
        {
            w.Write(route.Id);
            w.Write(route.Name);
            w.Write(route.PrincipalId);
            BackupBinary.WriteNullable(w, route.RemoteServiceName);
            BackupBinary.WriteNullable(w, route.BrokerInstance);
            w.Write(route.Lifetime?.Ticks ?? -1);
            w.Write(route.Address);
            BackupBinary.WriteNullable(w, route.MirrorAddress);
        }
        var bindings = objects.RemoteServiceBindings.Select(static pair => pair.Value).OrderBy(static binding => binding.Id).ToList();
        w.Write(bindings.Count);
        foreach (var binding in bindings)
        {
            w.Write(binding.Id);
            w.Write(binding.Name);
            w.Write(binding.PrincipalId);
            w.Write(binding.RemoteServiceName);
            w.Write(binding.RemotePrincipalId);
            w.Write(binding.Anonymous);
        }
        var priorities = objects.Priorities.Select(static pair => pair.Value).OrderBy(static priority => priority.Id).ToList();
        w.Write(priorities.Count);
        foreach (var priority in priorities)
        {
            w.Write(priority.Id);
            w.Write(priority.Name);
            BackupBinary.WriteNullableInt(w, priority.ContractId);
            BackupBinary.WriteNullableInt(w, priority.LocalServiceId);
            BackupBinary.WriteNullable(w, priority.RemoteServiceName);
            w.Write(priority.Level);
        }
    }

    private static void WriteQueue(BinaryWriter w, ServiceQueue queue)
    {
        w.Write(queue.IsMsShipped);
        w.Write(queue.Schema.Name);
        w.Write(queue.Name);
        w.Write(queue.ObjectId);
        w.Write(queue.InternalTableId);
        w.Write(queue.CreateDate.Ticks);
        w.Write(queue.ModifyDate.Ticks);
        BackupBinary.WriteNullableInt(w, queue.OwnerPrincipalId);
        var settings = queue.Settings;
        w.Write(settings.Enabled);
        w.Write(settings.Retention);
        w.Write(settings.PoisonMessageHandling);
        w.Write(settings.ActivationEnabled);
        BackupBinary.WriteNullable(w, settings.ActivationProcedure);
        BackupBinary.WriteNullable(w, settings.ActivationSchema);
        BackupBinary.WriteNullable(w, settings.ActivationName);
        w.Write(settings.MaxReaders);
        BackupBinary.WriteNullableInt(w, settings.ExecuteAsPrincipalId);
        w.Write(queue.NextQueuingOrder);
        w.Write(queue.ConsecutiveRollbacks);
        w.Write(queue.ReceivedCount);
        // A message an open transaction sent isn't committed; one it received
        // is still the queue's.
        var messages = queue.Messages.Where(static message => message.PendingOwner is null).ToList();
        w.Write(messages.Count);
        foreach (var message in messages)
        {
            w.Write(message.Status);
            w.Write(message.Priority);
            w.Write(message.QueuingOrder);
            w.Write(message.ConversationGroupId.ToByteArray());
            w.Write(message.ConversationHandle.ToByteArray());
            w.Write(message.SequenceNumber);
            w.Write(message.ServiceName);
            w.Write(message.ServiceId);
            w.Write(message.ContractName);
            w.Write(message.ContractId);
            w.Write(message.MessageTypeName);
            w.Write(message.MessageTypeId);
            w.Write(message.Validation);
            WriteOptionalBytes(w, message.Body);
            w.Write(message.EnqueueTime.Ticks);
        }
    }

    private static void WriteConversations(BinaryWriter w, Database database, BrokerRuntime? conversations)
    {
        var groups = conversations is null ? [] : conversations.Groups.Select(static pair => pair.Value)
            .Where(group => ReferenceEquals(group.Database, database) && group.PendingOwner is null).OrderBy(static group => group.Id).ToList();
        w.Write(groups.Count);
        foreach (var group in groups)
        {
            w.Write(group.Id.ToByteArray());
            w.Write(group.Service.Name);
        }
        var endpoints = conversations is null ? [] : conversations.Endpoints.Select(static pair => pair.Value)
            .Where(endpoint => ReferenceEquals(endpoint.Database, database) && endpoint.PendingOwner is null).OrderBy(static endpoint => endpoint.Handle).ToList();
        w.Write(endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            w.Write(endpoint.Handle.ToByteArray());
            w.Write(endpoint.ConversationId.ToByteArray());
            w.Write(endpoint.IsInitiator);
            w.Write(endpoint.Service.Name);
            w.Write(endpoint.Contract.Name);
            w.Write(endpoint.Group.Id.ToByteArray());
            w.Write(endpoint.FarService);
            BackupBinary.WriteNullable(w, endpoint.FarBrokerInstance);
            w.Write(endpoint.PrincipalId);
            w.Write(endpoint.FarPrincipalId);
            w.Write(endpoint.Lifetime.Ticks);
            w.Write(endpoint.Encrypted);
            w.Write((byte)endpoint.State);
            w.Write(endpoint.DialogTimer?.Ticks ?? -1);
            w.Write(endpoint.SendSequence);
            w.Write(endpoint.ReceiveSequence);
            w.Write(endpoint.EndDialogSequence);
            BackupBinary.WriteNullable(w, endpoint.HeldStatus);
            // The far side by its handle and, when it is in another database,
            // that database's name: the restore relinks what it finds.
            w.Write(endpoint.Far is not null);
            if (endpoint.Far is { } far)
            {
                BackupBinary.WriteNullable(w, ReferenceEquals(far.Database, database) ? null : far.Database.Name);
                w.Write(far.Handle.ToByteArray());
            }
        }
        var held = conversations is null ? [] : conversations.Transmission
            .Where(message => ReferenceEquals(message.From.Database, database) && message.PendingOwner is null && endpoints.Contains(message.From)).ToList();
        w.Write(held.Count);
        foreach (var message in held)
        {
            w.Write(message.From.Handle.ToByteArray());
            w.Write(message.Sequence);
            w.Write(message.MessageTypeName);
            w.Write(message.IsError);
            w.Write(message.IsEndOfDialog);
            WriteOptionalBytes(w, message.Body);
            w.Write(message.EnqueueTime.Ticks);
            w.Write(message.Status);
        }
    }

    /// <summary>The database's and its queues' event notifications, each naming its dialog's endpoint by handle.</summary>
    private static void WriteEventNotifications(BinaryWriter w, Database database)
    {
        var notifications = database.EventNotifications;
        w.Write(notifications.Length);
        foreach (var notification in notifications)
        {
            w.Write(notification.Name);
            w.Write(notification.ObjectId);
            w.Write((byte)notification.Parent);
            BackupBinary.WriteNullable(w, notification.Queue?.Schema.Name);
            BackupBinary.WriteNullable(w, notification.Queue?.Name);
            BackupBinary.WriteInts(w, [.. notification.Events.Select(static entry => entry.Type)]);
            BackupBinary.WriteInts(w, [.. notification.Groups.Select(static group => group ?? 0)]);
            w.Write(notification.CreateDate.Ticks);
            w.Write(notification.ServiceName);
            w.Write(notification.BrokerInstance);
            WriteOptionalBytes(w, notification.CreatorSid);
            w.Write(notification.PrincipalId);
            w.Write((notification.Dialog?.Handle ?? Guid.Empty).ToByteArray());
        }
    }

    private static void ReadEventNotifications(BinaryReader r, Database database, DatabaseBroker objects, BrokerRuntime runtime)
    {
        var notifications = new EventNotification[r.ReadInt32()];
        for (var i = 0; i < notifications.Length; i++)
        {
            var name = r.ReadString();
            var objectId = r.ReadInt32();
            var parent = (EventNotificationParent)r.ReadByte();
            var queueSchema = BackupBinary.ReadNullable(r);
            var queueName = BackupBinary.ReadNullable(r);
            var queue = queueSchema is null || queueName is null ? null : objects.FindQueue(database.Schemas[queueSchema], queueName);
            var events = Array.ConvertAll(BackupBinary.ReadInts(r), static type => EventNotificationEventTypes.TryResolveType(type, out var entry)
                ? entry
                : throw new InvalidDataException($"The backup set names the event type {type}, which the simulator doesn't know."));
            var groups = Array.ConvertAll(BackupBinary.ReadInts(r), static group => group == 0 ? null : (int?)group);
            notifications[i] = new EventNotification(name, objectId, parent, database, queue, events, groups, ReadTime(r), r.ReadString(), r.ReadString(), ReadOptionalBytes(r), r.ReadInt32())
            {
                Dialog = runtime.Endpoints.GetValueOrDefault(new(database, ReadGuid(r))),
            };
        }
        database.EventNotifications = notifications;
    }

    private static void WriteOptionalBytes(BinaryWriter w, byte[]? bytes)
    {
        w.Write(bytes?.Length ?? -1);
        if (bytes is not null)
            w.Write(bytes);
    }

    private static byte[]? ReadOptionalBytes(BinaryReader r) => r.ReadInt32() is var length and >= 0 ? r.ReadBytes(length) : null;

    private static DateTime ReadTime(BinaryReader r) => new(r.ReadInt64(), DateTimeKind.Unspecified);

    private static DateTime? ReadOptionalTime(BinaryReader r) => r.ReadInt64() is var ticks and >= 0 ? new DateTime(ticks, DateTimeKind.Unspecified) : null;

    private static Guid ReadGuid(BinaryReader r) => new(r.ReadBytes(16));

    /// <summary>
    /// Replaces <paramref name="database"/>'s Service Broker objects and
    /// conversations with the image's — everything it held before is gone,
    /// as the database it replaces is — under their original ids. The broker
    /// id comes back as backed up.
    /// </summary>
    private void ApplyBroker(Simulation simulation, Database database)
    {
        database.ServiceBrokerGuid = this.ServiceBrokerGuid;
        database.EventNotifications = [];
        foreach (var (_, schema) in database.Schemas)
            schema.Queues.Clear();
        var conversations = simulation.BrokerIfUsed;
        if (conversations is not null)
        {
            lock (conversations.Gate)
            {
                conversations.Forget(database);
                // The conversations of a database this restore replaced, which no statement reaches any more.
                foreach (var (_, endpoint) in conversations.Endpoints.ToList())
                {
                    if (!simulation.Databases.TryGetValue(endpoint.Database.Name, out var live) || !ReferenceEquals(live, endpoint.Database))
                        conversations.Forget(endpoint.Database);
                }
            }
        }
        if (this.BrokerState is not { } state)
        {
            database.ReplaceServiceBroker(null);
            return;
        }
        var objects = new DatabaseBroker(database);
        database.ReplaceServiceBroker(objects);
        using var r = new BinaryReader(new MemoryStream(state, writable: false), System.Text.Encoding.UTF8);
        ReadBrokerObjects(r, database, objects);
        var runtime = conversations ?? simulation.Broker;
        lock (runtime.Gate)
        {
            ReadConversations(r, simulation, database, objects, runtime);
            ReadEventNotifications(r, database, objects, runtime);
            Monitor.PulseAll(runtime.Gate);
        }
    }

    private static void ReadBrokerObjects(BinaryReader r, Database database, DatabaseBroker objects)
    {
        objects.RestoreIdCounters(BackupBinary.ReadInts(r));
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var id = r.ReadInt32();
            var name = r.ReadString();
            var principalId = r.ReadInt32();
            var validation = (MessageValidation)r.ReadByte();
            var collection = BackupBinary.ReadNullableInt(r) is { } collectionId ? FindXmlSchemaCollection(database, collectionId) : null;
            objects.MessageTypes[name] = new BrokerMessageType(id, name, principalId, validation, collection, isSystem: false);
        }
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var id = r.ReadInt32();
            var name = r.ReadString();
            var principalId = r.ReadInt32();
            var usages = new ContractUsage[r.ReadInt32()];
            for (var u = 0; u < usages.Length; u++)
                usages[u] = new ContractUsage(objects.MessageTypes[r.ReadString()], r.ReadBoolean(), r.ReadBoolean());
            objects.Contracts[name] = new BrokerContract(id, name, principalId, usages, isSystem: false);
        }
        for (var i = r.ReadInt32(); i > 0; i--)
            ReadQueue(r, database, objects);
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var id = r.ReadInt32();
            var name = r.ReadString();
            var principalId = r.ReadInt32();
            var queue = objects.FindQueue(database.Schemas[r.ReadString()], r.ReadString())
                ?? throw new InvalidDataException($"The backup set's service '{name}' names a queue it doesn't carry.");
            var contracts = new BrokerContract[r.ReadInt32()];
            for (var c = 0; c < contracts.Length; c++)
                contracts[c] = objects.Contracts[r.ReadString()];
            objects.Services[name] = new BrokerService(id, name, principalId, queue, contracts, isSystem: false);
        }
        objects.Routes.Clear();
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var route = new BrokerRoute(r.ReadInt32(), r.ReadString(), r.ReadInt32())
            {
                RemoteServiceName = BackupBinary.ReadNullable(r),
                BrokerInstance = BackupBinary.ReadNullable(r),
                Lifetime = ReadOptionalTime(r),
                Address = r.ReadString(),
                MirrorAddress = BackupBinary.ReadNullable(r),
            };
            objects.Routes[route.Name] = route;
        }
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var binding = new RemoteServiceBinding(r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadBoolean());
            objects.RemoteServiceBindings[binding.Name] = binding;
        }
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var priority = new BrokerPriority(r.ReadInt32(), r.ReadString())
            {
                ContractId = BackupBinary.ReadNullableInt(r),
                LocalServiceId = BackupBinary.ReadNullableInt(r),
                RemoteServiceName = BackupBinary.ReadNullable(r),
                Level = r.ReadByte(),
            };
            objects.Priorities[priority.Name] = priority;
        }
    }

    private static XmlSchemaCollection? FindXmlSchemaCollection(Database database, int id)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, collection) in schema.XmlSchemaCollections)
            {
                if (collection.Id == id)
                    return collection;
            }
        }
        return null;
    }

    private static void ReadQueue(BinaryReader r, Database database, DatabaseBroker objects)
    {
        var isSystem = r.ReadBoolean();
        var schema = database.Schemas[r.ReadString()];
        var name = r.ReadString();
        var objectId = r.ReadInt32();
        var internalTableId = r.ReadInt32();
        var createDate = ReadTime(r);
        var modifyDate = ReadTime(r);
        var owner = BackupBinary.ReadNullableInt(r);
        var settings = new QueueSettings
        {
            Enabled = r.ReadBoolean(),
            Retention = r.ReadBoolean(),
            PoisonMessageHandling = r.ReadBoolean(),
            ActivationEnabled = r.ReadBoolean(),
            ActivationProcedure = BackupBinary.ReadNullable(r),
            ActivationSchema = BackupBinary.ReadNullable(r),
            ActivationName = BackupBinary.ReadNullable(r),
            MaxReaders = r.ReadInt16(),
            ExecuteAsPrincipalId = BackupBinary.ReadNullableInt(r),
        };
        ServiceQueue queue;
        if (isSystem)
        {
            queue = objects.FindQueue(schema, name) ?? throw new InvalidDataException($"The backup set names the shipped queue '{name}', which the database lacks.");
        }
        else
        {
            queue = new ServiceQueue(schema, name, objectId, createDate, settings) { InternalTableId = internalTableId, OwnerPrincipalId = owner, ModifyDate = modifyDate };
            schema.Queues[name] = queue;
        }
        queue.NextQueuingOrder = r.ReadInt64();
        queue.ConsecutiveRollbacks = r.ReadInt32();
        queue.ReceivedCount = r.ReadInt64();
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            queue.Messages.Add(new QueuedMessage
            {
                Status = r.ReadByte(),
                Priority = r.ReadByte(),
                QueuingOrder = r.ReadInt64(),
                ConversationGroupId = ReadGuid(r),
                ConversationHandle = ReadGuid(r),
                SequenceNumber = r.ReadInt64(),
                ServiceName = r.ReadString(),
                ServiceId = r.ReadInt32(),
                ContractName = r.ReadString(),
                ContractId = r.ReadInt32(),
                MessageTypeName = r.ReadString(),
                MessageTypeId = r.ReadInt32(),
                Validation = r.ReadChar(),
                Body = ReadOptionalBytes(r),
                EnqueueTime = ReadTime(r),
            });
        }
    }

    private static void ReadConversations(BinaryReader r, Simulation simulation, Database database, DatabaseBroker objects, BrokerRuntime runtime)
    {
        var groups = new Dictionary<Guid, ConversationGroup>();
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var id = ReadGuid(r);
            var group = new ConversationGroup(id, database, objects.Services[r.ReadString()]);
            groups[id] = group;
            runtime.Groups[group.Key] = group;
        }
        var links = new List<(ConversationEndpoint Endpoint, string? Database, Guid Handle)>();
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var handle = ReadGuid(r);
            var conversationId = ReadGuid(r);
            var isInitiator = r.ReadBoolean();
            var service = objects.Services[r.ReadString()];
            var contract = objects.Contracts[r.ReadString()];
            var group = groups[ReadGuid(r)];
            var farService = r.ReadString();
            var farInstance = BackupBinary.ReadNullable(r);
            var principalId = r.ReadInt32();
            var farPrincipalId = r.ReadInt32();
            var lifetime = ReadTime(r);
            var encrypted = r.ReadBoolean();
            var endpoint = new ConversationEndpoint(handle, conversationId, isInitiator, database, service, contract, group, farService, principalId, lifetime, encrypted)
            {
                FarBrokerInstance = farInstance,
                FarPrincipalId = farPrincipalId,
                State = (EndpointState)r.ReadByte(),
                DialogTimer = ReadOptionalTime(r),
                SendSequence = r.ReadInt64(),
                ReceiveSequence = r.ReadInt64(),
                EndDialogSequence = r.ReadInt64(),
                HeldStatus = BackupBinary.ReadNullable(r),
            };
            runtime.Endpoints[endpoint.Key] = endpoint;
            if (r.ReadBoolean())
                links.Add((endpoint, BackupBinary.ReadNullable(r), ReadGuid(r)));
        }
        foreach (var (endpoint, farDatabase, farHandle) in links)
        {
            if (farDatabase is null)
            {
                if (runtime.Endpoints.TryGetValue(new(database, farHandle), out var near))
                    endpoint.Far = near;
                continue;
            }
            // A far side in another database takes this endpoint back only
            // while it still names the endpoint this one replaces.
            if (simulation.Databases.TryGetValue(farDatabase, out var other) && runtime.Endpoints.TryGetValue(new(other, farHandle), out var far)
                && far.Far is { } named && named.Handle == endpoint.Handle && named.Database.Name.Equals(database.Name, StringComparison.OrdinalIgnoreCase))
            {
                endpoint.Far = far;
                far.Far = endpoint;
            }
        }
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var from = runtime.Endpoints[new(database, ReadGuid(r))];
            runtime.Transmission.Add(new TransmissionMessage
            {
                From = from,
                Sequence = r.ReadInt64(),
                MessageTypeName = r.ReadString(),
                IsError = r.ReadBoolean(),
                IsEndOfDialog = r.ReadBoolean(),
                Body = ReadOptionalBytes(r),
                EnqueueTime = ReadTime(r),
                Status = r.ReadString(),
            });
        }
    }

    private void WriteBroker(BinaryWriter w)
    {
        w.Write(this.ServiceBrokerGuid.ToByteArray());
        WriteOptionalBytes(w, this.BrokerState);
    }

    private static void ReadBroker(BinaryReader r, DatabaseImage image)
    {
        image.ServiceBrokerGuid = ReadGuid(r);
        image.BrokerState = ReadOptionalBytes(r);
    }
}
