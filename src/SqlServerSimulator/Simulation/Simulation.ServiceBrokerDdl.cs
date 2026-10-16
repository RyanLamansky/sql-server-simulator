using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// CREATE / ALTER / DROP of the Service Broker objects: message types,
// contracts, queues, services, routes, remote service bindings and broker
// priorities. Grammar, catalog effects and refusals were probed 2026-10-16
// against SQL Server 2025; see docs/claude/service-broker.md.
partial class Simulation
{
    /// <summary>The Service Broker object kinds a <c>CREATE</c> / <c>ALTER</c> / <c>DROP</c> names.</summary>
    private enum BrokerObjectKind
    {
        None,
        MessageType,
        Contract,
        Queue,
        Service,
        Route,
        RemoteServiceBinding,
        BrokerPriority,
        EventNotification,
    }

    /// <summary>
    /// The Service Broker kind the words at the cursor (the one after the
    /// verb) name, consuming them so the cursor rests on the last; the
    /// cursor stays put when they name none.
    /// </summary>
    private static BrokerObjectKind ReadBrokerObjectKind(ParserContext context)
    {
        if (context.Token is not UnquotedString { Value: var word })
            return BrokerObjectKind.None;
        var checkpoint = context.SaveCheckpoint();
        BrokerObjectKind Followed(string next, BrokerObjectKind kind)
        {
            if (context.MoveNext() && context.Token is UnquotedString { Value: var second } && BuiltInToken.Equals(second, next))
                return kind;
            context.RestoreCheckpoint(checkpoint);
            return BrokerObjectKind.None;
        }
        return BuiltInToken.Equals(word, "BROKER") ? Followed("PRIORITY", BrokerObjectKind.BrokerPriority)
            : BuiltInToken.Equals(word, "CONTRACT") ? BrokerObjectKind.Contract
            : BuiltInToken.Equals(word, "EVENT") ? Followed("NOTIFICATION", BrokerObjectKind.EventNotification)
            : BuiltInToken.Equals(word, "MESSAGE") ? Followed("TYPE", BrokerObjectKind.MessageType)
            : BuiltInToken.Equals(word, "QUEUE") ? BrokerObjectKind.Queue
            : BuiltInToken.Equals(word, "ROUTE") ? BrokerObjectKind.Route
            : BuiltInToken.Equals(word, "SERVICE") ? BrokerObjectKind.Service
            : BrokerObjectKind.None;
    }

    /// <summary>
    /// Parses and runs a Service Broker DDL statement when the words after
    /// <paramref name="verb"/> name a broker object kind; false otherwise,
    /// the cursor untouched. Cursor on entry: the word after the verb.
    /// </summary>
    private static bool TryParseBrokerDdl(ParserContext context, string verb)
    {
        var kind = ReadRemoteAwareBrokerKind(context);
        if (kind == BrokerObjectKind.None)
            return false;
        switch (verb, kind)
        {
            case ("CREATE", BrokerObjectKind.MessageType):
                CreateMessageType(context);
                break;
            case ("ALTER", BrokerObjectKind.MessageType):
                AlterMessageType(context);
                break;
            case ("CREATE", BrokerObjectKind.Contract):
                CreateContract(context);
                break;
            case ("CREATE", BrokerObjectKind.Queue):
                CreateQueue(context);
                break;
            case ("ALTER", BrokerObjectKind.Queue):
                AlterQueue(context);
                break;
            case ("CREATE", BrokerObjectKind.Service):
                CreateService(context);
                break;
            case ("ALTER", BrokerObjectKind.Service):
                AlterService(context);
                break;
            case ("CREATE" or "ALTER", BrokerObjectKind.Route):
                CreateOrAlterRoute(context, verb == "ALTER");
                break;
            case ("CREATE" or "ALTER", BrokerObjectKind.RemoteServiceBinding):
                CreateOrAlterRemoteServiceBinding(context, verb == "ALTER");
                break;
            case ("CREATE" or "ALTER", BrokerObjectKind.BrokerPriority):
                CreateOrAlterBrokerPriority(context, verb == "ALTER");
                break;
            case ("CREATE", BrokerObjectKind.EventNotification):
                CreateEventNotification(context);
                break;
            case ("DROP", BrokerObjectKind.EventNotification):
                DropEventNotification(context);
                break;
            case ("DROP", _):
                DropBrokerObject(context, kind);
                break;
            default:
                // ALTER CONTRACT and ALTER EVENT NOTIFICATION: real has no such statement.
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        return true;
    }

    // REMOTE SERVICE BINDING is three words; the others one or two.
    private static BrokerObjectKind ReadRemoteAwareBrokerKind(ParserContext context)
    {
        if (context.Token is UnquotedString { Value: var word } && BuiltInToken.Equals(word, "REMOTE"))
        {
            var checkpoint = context.SaveCheckpoint();
            if (context.MoveNext() && context.Token is UnquotedString { Value: var second } && BuiltInToken.Equals(second, "SERVICE")
                && context.MoveNext() && context.Token is UnquotedString { Value: var third } && BuiltInToken.Equals(third, "BINDING"))
            {
                return BrokerObjectKind.RemoteServiceBinding;
            }
            context.RestoreCheckpoint(checkpoint);
            return BrokerObjectKind.None;
        }
        return ReadBrokerObjectKind(context);
    }

    private static bool AtWord(ParserContext context, string word) =>
        context.Token is UnquotedString { Value: var value } && BuiltInToken.Equals(value, word);

    /// <summary>Moves past the current token and requires the next to be <paramref name="word"/>.</summary>
    private static void RequireNextWord(ParserContext context, string word)
    {
        context.MoveNextRequired();
        if (!AtWord(context, word))
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>Moves past the current token and requires the next to be the operator <paramref name="character"/>.</summary>
    private static void RequireNextOperator(ParserContext context, char character)
    {
        if (context.GetNextRequired() is not Operator op || op.Character != character)
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>A broker object's name at the cursor (a reserved word is Msg 156), left on it.</summary>
    private static string ReadBrokerName(ParserContext context) => context.Token switch
    {
        Name name => name.Value,
        ReservedKeyword keyword => throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// The optional <c>AUTHORIZATION owner</c> after a broker object's name,
    /// the cursor moved past what it read; the owner's principal id, or the
    /// creating principal's.
    /// </summary>
    private static int ReadBrokerOwner(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Authorization })
            return context.Connection.Security.Effective.DatabasePrincipalId;
        context.MoveNextRequired();
        var ownerName = ReadBrokerName(context);
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return Database.DboPrincipalId;
        return context.CurrentDatabase.Principals.TryGetValue(ownerName, out var owner)
            ? owner.PrincipalId
            : throw SimulatedSqlException.CannotFindUser(ownerName);
    }

    /// <summary>
    /// The database-scope gate a broker <c>CREATE</c> passes, Msg 262 at
    /// <paramref name="state"/> without it, and the read-only refusal.
    /// </summary>
    private static void RequireBrokerCreatePermission(ParserContext context, string permission, byte state)
    {
        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HoldsDatabasePermission(context.Batch, database, permission))
            throw SimulatedSqlException.BrokerCreatePermissionDenied(permission, database.Name, state);
    }

    /// <summary>
    /// Whether the effective principal may alter or drop a database-scoped
    /// broker object: the <c>ALTER ANY …</c> permission of its class, or
    /// <c>CONTROL</c> on it, which its owner holds.
    /// </summary>
    private static bool MayChangeBrokerObject(ParserContext context, string alterAny, byte securableClass, int id, int ownerId)
    {
        var batch = context.Batch;
        var database = context.CurrentDatabase;
        return PermissionEnforcement.HoldsDatabasePermission(batch, database, alterAny)
            || PermissionEnforcement.HoldsPermission(batch, database, Permission.Control, securableClass, id, 0)
            || PermissionEnforcement.MayActAs(batch, database, ownerId);
    }

    // ---- message types ----

    private static void CreateMessageType(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        context.MoveNextOptional();
        var ownerId = ReadBrokerOwner(context);
        var (validation, collection) = ReadMessageValidation(context, required: false);
        if (context.Batch.IsSkipping)
            return;
        RequireBrokerCreatePermission(context, "CREATE MESSAGE TYPE", 17);
        var broker = context.CurrentDatabase.ServiceBroker;
        if (broker.MessageTypes.ContainsKey(name))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 10);
        var type = new BrokerMessageType(broker.AllocateMessageTypeId(), name, ownerId, validation, collection, isSystem: false);
        if (!broker.MessageTypes.TryAdd(name, type))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 10);
        RecordSlotUndo<BrokerMessageType>(context, broker.MessageTypes, name, null);
        RecordDdlEvent(context, "CREATE_MESSAGE_TYPE", schemaName: null, name, "MESSAGE TYPE");
    }

    private static void AlterMessageType(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        context.MoveNextRequired();
        var (validation, collection) = ReadMessageValidation(context, required: true);
        if (context.Batch.IsSkipping)
            return;
        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        var broker = database.ServiceBroker;
        if (!broker.MessageTypes.TryGetValue(name, out var type)
            || !MayChangeBrokerObject(context, "ALTER ANY MESSAGE TYPE", 15, type.Id, type.PrincipalId))
        {
            throw SimulatedSqlException.BrokerObjectNotFound("alter", "message type", name);
        }
        if (type.IsSystem)
            throw SimulatedSqlException.SystemBrokerObjectCannotBeModified(1);
        var (oldValidation, oldCollection) = (type.Validation, type.Collection);
        type.Validation = validation;
        type.Collection = collection;
        RecordDdlUndo(context, () =>
        {
            type.Validation = oldValidation;
            type.Collection = oldCollection;
        });
        RecordDdlEvent(context, "ALTER_MESSAGE_TYPE", schemaName: null, name, "MESSAGE TYPE");
    }

    /// <summary>
    /// Reads <c>VALIDATION = { NONE | EMPTY | WELL_FORMED_XML | VALID_XML WITH
    /// SCHEMA COLLECTION name }</c>, the cursor on <c>VALIDATION</c> and left
    /// after the clause; <c>NONE</c> when it is absent and not
    /// <paramref name="required"/>. A missing collection is Msg 15151, raised
    /// as the statement runs.
    /// </summary>
    private static (MessageValidation, XmlSchemaCollection?) ReadMessageValidation(ParserContext context, bool required)
    {
        if (!AtWord(context, "VALIDATION"))
        {
            return required ? throw SimulatedSqlException.SyntaxErrorNear(context) : (MessageValidation.None, null);
        }
        RequireNextOperator(context, '=');
        context.MoveNextRequired();
        MessageValidation validation;
        XmlSchemaCollection? collection = null;
        if (AtWord(context, "NONE"))
        {
            validation = MessageValidation.None;
        }
        else if (AtWord(context, "EMPTY"))
        {
            validation = MessageValidation.Empty;
        }
        else if (AtWord(context, "WELL_FORMED_XML"))
        {
            validation = MessageValidation.WellFormedXml;
        }
        else if (AtWord(context, "VALID_XML"))
        {
            validation = MessageValidation.ValidXml;
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.With })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Schema })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            RequireNextWord(context, "COLLECTION");
            context.MoveNextRequired();
            var collectionName = BatchContext.ParseObjectName(context);
            if (!context.Batch.IsSkipping)
            {
                if (!context.Batch.TryResolveXmlSchemaCollectionSchema(collectionName, out var schema)
                    || !schema.XmlSchemaCollections.TryGetValue(collectionName.Leaf, out collection))
                {
                    throw SimulatedSqlException.CannotFindXmlSchemaCollection(collectionName.Leaf);
                }
            }
        }
        else
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return (validation, collection);
    }

    // ---- contracts ----

    private static void CreateContract(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        context.MoveNextRequired();
        var ownerId = ReadBrokerOwner(context);
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var written = new List<(string Type, bool Initiator, bool Target)>();
        do
        {
            context.MoveNextRequired();
            var typeName = ReadBrokerName(context);
            if (context.GetNextRequired() is not UnquotedString { Value: var sent } || !BuiltInToken.Equals(sent, "SENT"))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            var (initiator, target) = context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.Any } => (true, true),
                UnquotedString { Value: var side } when BuiltInToken.Equals(side, "INITIATOR") => (true, false),
                UnquotedString { Value: var side } when BuiltInToken.Equals(side, "TARGET") => (false, true),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            written.Add((typeName, initiator, target));
            context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return;

        RequireBrokerCreatePermission(context, "CREATE CONTRACT", 17);
        var broker = context.CurrentDatabase.ServiceBroker;
        if (broker.Contracts.ContainsKey(name))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 11);
        var usages = new List<ContractUsage>(written.Count);
        foreach (var (typeName, initiator, target) in written)
        {
            if (!broker.MessageTypes.TryGetValue(typeName, out var type))
                throw SimulatedSqlException.BrokerObjectNotFound("find", "message type", typeName);
            if (usages.Exists(usage => ReferenceEquals(usage.MessageType, type)))
                throw SimulatedSqlException.MessageTypeSpecifiedTwice(typeName);
            usages.Add(new ContractUsage(type, initiator, target));
        }
        var contract = new BrokerContract(broker.AllocateContractId(), name, ownerId, [.. usages], isSystem: false);
        if (!broker.Contracts.TryAdd(name, contract))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 11);
        RecordSlotUndo<BrokerContract>(context, broker.Contracts, name, null);
        RecordDdlEvent(context, "CREATE_CONTRACT", schemaName: null, name, "CONTRACT");
    }

    // ---- queues ----

    /// <summary>The <c>WITH</c> options of a <c>CREATE</c> or <c>ALTER QUEUE</c> as written; null where absent.</summary>
    private sealed class QueueOptions
    {
        public bool? Status, Retention, PoisonMessageHandling, ActivationStatus;
        public bool Activation, ActivationDrop;
        public MultiPartName? ActivationProcedure;
        public int? MaxReaders;

        /// <summary><c>SELF</c>, <c>OWNER</c> or a user's name, as written.</summary>
        public string? ExecuteAs;

        public bool ExecuteAsUser;
    }

    private static void CreateQueue(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var nameToken = context.Token;
        var queueName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();
        var options = new QueueOptions();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            ReadQueueOptions(context, options, alter: false, nameToken);
        string? filegroup = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            context.MoveNextRequired();
            filegroup = context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.Default } => "DEFAULT",
                Name fg => fg.Value,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextOptional();
        }
        if (context.Batch.IsSkipping)
            return;

        var batch = context.Batch;
        if (!batch.TryResolveCreateSchema(queueName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(queueName.ImmediateQualifier ?? Database.DefaultSchemaName);
        var database = schema.Database;
        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HoldsDatabasePermission(batch, database, "CREATE QUEUE"))
            throw SimulatedSqlException.BrokerCreatePermissionDenied("CREATE QUEUE", database.Name, 18);
        if (!PermissionEnforcement.HasSchemaAlterForCreate(batch, schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
        if (filegroup is not null && !filegroup.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) && !database.Filegroups.ContainsKey(filegroup))
            throw SimulatedSqlException.QueueFilegroupInvalid(filegroup);

        var settings = new QueueSettings
        {
            Enabled = options.Status ?? true,
            Retention = options.Retention ?? false,
            PoisonMessageHandling = options.PoisonMessageHandling ?? true,
        };
        if (options.Activation)
            ApplyActivation(context, schema, options, ref settings);

        batch.LockDefinitionName(schema, queueName.Leaf, DefinitionNameUse.Creates);
        var queue = new ServiceQueue(schema, queueName.Leaf, database.AllocateObjectId(), batch.CurrentStatement.UtcNow, settings)
        {
            InternalTableId = database.AllocateObjectId(),
        };
        batch.LockDefinition(schema, queue, created: true);
        if (schema.HasNameInSharedNamespace(queue.Name) || !schema.Queues.TryAdd(queue.Name, queue))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(queueName.Leaf, 14);
        RecordSlotUndo<ServiceQueue>(context, schema.Queues, queue.Name, null);
        RecordDdlEvent(context, "CREATE_QUEUE", schema.Name, queue.Name, "QUEUE");
    }

    private static void AlterQueue(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var nameToken = context.Token;
        var queueName = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();
        var options = new QueueOptions();
        var maintenance = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            ReadQueueOptions(context, options, alter: true, nameToken);
        }
        else if (AtWord(context, "REBUILD") || AtWord(context, "REORGANIZE"))
        {
            // REBUILD [WITH (MAXDOP = n)] / REORGANIZE [WITH (LOB_COMPACTION =
            // ON | OFF)]: the queue's internal table has no pages here to move.
            maintenance = true;
            context.MoveNextOptional();
            if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            {
                RequireNextOperator(context, '(');
                SkipParenthesized(context);
            }
        }
        else if (AtWord(context, "MOVE"))
        {
            maintenance = true;
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.To })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is not (Name or ReservedKeyword { Keyword: Keyword.Default }))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        else
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.Batch.IsSkipping)
            return;

        var batch = context.Batch;
        var queue = ResolveQueueForDdl(context, queueName, "alter");
        var schema = queue.Schema;
        schema.Database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasObjectAlter(batch, schema.Database, queue.ObjectId, queue.SchemaId))
            throw SimulatedSqlException.BrokerObjectNotFound("alter", "queue", queueName.ToString());
        if (queue.IsMsShipped)
            throw SimulatedSqlException.SystemBrokerObjectCannotBeModified(3);
        batch.LockDefinitionName(schema, queue.Name);
        batch.LockDefinition(schema, queue);
        if (!maintenance)
        {
            var before = queue.Settings;
            var after = before;
            if (options.Status is { } status)
            {
                after.Enabled = status;
                // Re-enabling a queue starts its rollback count afresh.
                if (status)
                    queue.ConsecutiveRollbacks = 0;
            }
            if (options.Retention is { } retention)
                after.Retention = retention;
            if (options.PoisonMessageHandling is { } poison)
                after.PoisonMessageHandling = poison;
            if (options.ActivationDrop)
            {
                after.ActivationEnabled = false;
                after.ActivationProcedure = after.ActivationSchema = after.ActivationName = null;
                after.MaxReaders = 0;
                after.ExecuteAsPrincipalId = null;
            }
            else if (options.Activation)
            {
                ApplyActivation(context, schema, options, ref after);
            }
            var modified = queue.ModifyDate;
            queue.Settings = after;
            queue.ModifyDate = batch.CurrentStatement.UtcNow;
            RecordDdlUndo(context, () =>
            {
                queue.Settings = before;
                queue.ModifyDate = modified;
            });
        }
        RecordDdlEvent(context, "ALTER_QUEUE", schema.Name, queue.Name, "QUEUE");
        // Turning a queue back on delivers what it refused; a message for a
        // queue still off is held again.
        if (options.Status is true)
            batch.Connection.Simulation.RedeliverHeld(batch, null, QueueRefusedStatus);
        // A queue whose activation is turned on with messages waiting starts
        // its reader.
        if (!maintenance && queue.Settings.ActivationEnabled)
            batch.Connection.Simulation.KickActivation(queue, schema.Database);
    }

    /// <summary>
    /// The queue <paramref name="name"/> resolves to — a user queue or one
    /// real ships — or Msg 15151 naming it with <paramref name="verb"/>.
    /// </summary>
    private static ServiceQueue ResolveQueueForDdl(ParserContext context, MultiPartName name, string verb)
    {
        if (context.Batch.TryResolveSchema(name, out var schema) && schema.Database.ServiceBroker.FindQueue(schema, name.Leaf) is { } queue)
            return queue;
        throw SimulatedSqlException.BrokerObjectNotFound(verb, "queue", name.ToString());
    }

    /// <summary>
    /// Reads a queue's <c>WITH</c> list — <c>STATUS</c>, <c>RETENTION</c>,
    /// <c>ACTIVATION (…)</c> and <c>POISON_MESSAGE_HANDLING (STATUS = …)</c>,
    /// commas between them optional — the cursor on <c>WITH</c> and left after
    /// the list. A <c>CREATE</c>'s activation must name its procedure, its
    /// reader count and its <c>EXECUTE AS</c>, which real reports as a syntax
    /// error at the queue's name.
    /// </summary>
    private static void ReadQueueOptions(ParserContext context, QueueOptions options, bool alter, Token queueNameToken)
    {
        context.MoveNextRequired();
        while (true)
        {
            if (AtWord(context, "STATUS"))
            {
                options.Status = ReadOnOffAfterEquals(context);
            }
            else if (AtWord(context, "RETENTION"))
            {
                options.Retention = ReadOnOffAfterEquals(context);
            }
            else if (AtWord(context, "POISON_MESSAGE_HANDLING"))
            {
                RequireNextOperator(context, '(');
                RequireNextWord(context, "STATUS");
                options.PoisonMessageHandling = ReadOnOffAfterEquals(context);
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
            }
            else if (AtWord(context, "ACTIVATION"))
            {
                RequireNextOperator(context, '(');
                context.MoveNextRequired();
                if (alter && context.Token is ReservedKeyword { Keyword: Keyword.Drop })
                {
                    options.ActivationDrop = true;
                    context.MoveNextRequired();
                }
                else
                {
                    options.Activation = true;
                    while (true)
                    {
                        if (AtWord(context, "STATUS"))
                        {
                            options.ActivationStatus = ReadOnOffAfterEquals(context);
                        }
                        else if (AtWord(context, "PROCEDURE_NAME"))
                        {
                            RequireNextOperator(context, '=');
                            context.MoveNextRequired();
                            options.ActivationProcedure = BatchContext.ParseObjectName(context);
                            context.MoveNextRequired();
                        }
                        else if (AtWord(context, "MAX_QUEUE_READERS"))
                        {
                            RequireNextOperator(context, '=');
                            if (context.GetNextRequired() is not Numeric number)
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            var value = number.Value.CoerceTo(SqlType.BigInt).AsInt64;
                            if (value is < 0 or > 32767)
                                throw SimulatedSqlException.MaxQueueReadersOutOfRange();
                            options.MaxReaders = (int)value;
                            context.MoveNextRequired();
                        }
                        else if (context.Token is ReservedKeyword { Keyword: Keyword.Execute })
                        {
                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            context.MoveNextRequired();
                            switch (context.Token)
                            {
                                case UnquotedString { Value: var who } when BuiltInToken.EqualsAny(who, "SELF", "OWNER"):
                                    options.ExecuteAs = who.ToUpperInvariant();
                                    break;
                                case Literal { Value: { IsNull: false } literal }:
                                    options.ExecuteAs = literal.AsString;
                                    options.ExecuteAsUser = true;
                                    break;
                                default:
                                    throw SimulatedSqlException.SyntaxErrorNear(context);
                            }
                            context.MoveNextRequired();
                        }
                        else
                        {
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        }
                        if (context.Token is Operator { Character: ',' })
                        {
                            context.MoveNextRequired();
                            continue;
                        }
                        break;
                    }
                    if (!alter && (options.ActivationProcedure is null || options.MaxReaders is null || options.ExecuteAs is null))
                        throw SimulatedSqlException.SyntaxErrorNear(queueNameToken);
                }
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            if (AtWord(context, "STATUS") || AtWord(context, "RETENTION") || AtWord(context, "ACTIVATION") || AtWord(context, "POISON_MESSAGE_HANDLING"))
                continue;
            return;
        }
    }

    /// <summary>Reads <c>= ON | OFF</c> after an option word, the cursor left after the value.</summary>
    private static bool ReadOnOffAfterEquals(ParserContext context)
    {
        RequireNextOperator(context, '=');
        context.MoveNextRequired();
        var value = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.On } => true,
            ReservedKeyword { Keyword: Keyword.Off } => false,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextOptional();
        return value;
    }

    /// <summary>
    /// Applies an <c>ACTIVATION</c> clause to <paramref name="settings"/>:
    /// the procedure resolves in the queue's database (Msg 2812 state 64 when
    /// missing), <c>SELF</c> stores the creating principal, <c>OWNER</c> -2
    /// and a named user that user's id. A clause naming no <c>STATUS</c>
    /// turns activation on (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static void ApplyActivation(ParserContext context, Schema queueSchema, QueueOptions options, ref QueueSettings settings)
    {
        var database = queueSchema.Database;
        var batch = context.Batch;
        var hadProcedure = settings.ActivationProcedure is not null;
        if (options.ActivationProcedure is { } procedureName)
        {
            var procedureSchemaName = procedureName.ImmediateQualifier ?? Database.DefaultSchemaName;
            if (!database.Schemas.TryGetValue(procedureSchemaName, out var procedureSchema)
                || !procedureSchema.Procedures.TryGetValue(procedureName.Leaf, out var procedure))
            {
                throw SimulatedSqlException.ActivationProcedureNotFound(procedureName.Leaf);
            }
            settings.ActivationProcedure = $"[{procedureSchema.Name}].[{procedure.Name}]";
            settings.ActivationSchema = procedureSchema.Name;
            settings.ActivationName = procedure.Name;
        }
        if (options.MaxReaders is { } readers)
            settings.MaxReaders = (short)readers;
        if (options.ExecuteAs is { } executeAs)
        {
            settings.ExecuteAsPrincipalId = options.ExecuteAsUser
                ? database.Principals.TryGetValue(executeAs, out var user) ? user.PrincipalId : throw SimulatedSqlException.CannotFindUser(executeAs)
                : executeAs == "OWNER" ? -2 : batch.Connection.Security.Effective.DatabasePrincipalId;
        }
        settings.ActivationEnabled = options.ActivationStatus ?? (!hadProcedure || settings.ActivationEnabled);
    }

    // ---- services ----

    private static void CreateService(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        context.MoveNextRequired();
        var ownerId = ReadBrokerOwner(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        RequireNextWord(context, "QUEUE");
        context.MoveNextRequired();
        var queueName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();
        var contractNames = new List<string>();
        if (context.Token is Operator { Character: '(' })
        {
            do
            {
                context.MoveNextRequired();
                contractNames.Add(ReadBrokerName(context));
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        if (context.Batch.IsSkipping)
            return;

        RequireBrokerCreatePermission(context, "CREATE SERVICE", 17);
        var broker = context.CurrentDatabase.ServiceBroker;
        if (broker.Services.ContainsKey(name))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 13);
        var queue = ResolveQueueForDdl(context, queueName, "find");
        var contracts = new List<BrokerContract>(contractNames.Count);
        foreach (var contractName in contractNames)
        {
            if (!broker.Contracts.TryGetValue(contractName, out var contract))
                throw SimulatedSqlException.BrokerObjectNotFound("find", "contract", contractName);
            if (contracts.Contains(contract))
                throw SimulatedSqlException.ServiceContractSpecifiedTwice(contractName);
            contracts.Add(contract);
        }
        var service = new BrokerService(broker.AllocateServiceId(), name, ownerId, queue, [.. contracts], isSystem: false);
        if (!broker.Services.TryAdd(name, service))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 13);
        RecordSlotUndo<BrokerService>(context, broker.Services, name, null);
        RecordDdlEvent(context, "CREATE_SERVICE", schemaName: null, name, "SERVICE");
    }

    private static void AlterService(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        context.MoveNextOptional();
        MultiPartName? queueName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            RequireNextWord(context, "QUEUE");
            context.MoveNextRequired();
            queueName = BatchContext.ParseObjectName(context);
            context.MoveNextOptional();
        }
        var changes = new List<(bool Add, string Contract)>();
        if (context.Token is Operator { Character: '(' })
        {
            do
            {
                context.MoveNextRequired();
                var add = context.Token switch
                {
                    ReservedKeyword { Keyword: Keyword.Add } => true,
                    ReservedKeyword { Keyword: Keyword.Drop } => false,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                RequireNextWord(context, "CONTRACT");
                context.MoveNextRequired();
                changes.Add((add, ReadBrokerName(context)));
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        if (queueName is null && changes.Count == 0)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.Batch.IsSkipping)
            return;

        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        var broker = database.ServiceBroker;
        if (!broker.Services.TryGetValue(name, out var service)
            || !MayChangeBrokerObject(context, "ALTER ANY SERVICE", 17, service.Id, service.PrincipalId))
        {
            throw SimulatedSqlException.BrokerObjectNotFound("alter", "service", name);
        }
        if (service.IsSystem)
            throw SimulatedSqlException.SystemBrokerObjectCannotBeModified(6);
        var queue = queueName is { } written ? ResolveQueueForDdl(context, written, "find") : service.Queue;
        var contracts = new List<BrokerContract>(service.Contracts);
        foreach (var (add, contractName) in changes)
        {
            if (!broker.Contracts.TryGetValue(contractName, out var contract))
                throw SimulatedSqlException.BrokerObjectNotFound("find", "contract", contractName);
            if (add)
            {
                if (contracts.Contains(contract))
                    throw SimulatedSqlException.ServiceContractSpecifiedTwice(contractName);
                contracts.Add(contract);
            }
            else if (!contracts.Remove(contract))
            {
                throw SimulatedSqlException.BrokerObjectNotFound("find", "contract", contractName);
            }
        }
        var (oldQueue, oldContracts) = (service.Queue, service.Contracts);
        service.Queue = queue;
        service.Contracts = [.. contracts];
        RecordDdlUndo(context, () =>
        {
            service.Queue = oldQueue;
            service.Contracts = oldContracts;
        });
        RecordDdlEvent(context, "ALTER_SERVICE", schemaName: null, name, "SERVICE");
    }

    // ---- routes ----

    private static void CreateOrAlterRoute(ParserContext context, bool alter)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        context.MoveNextRequired();
        var ownerId = alter ? 0 : ReadBrokerOwner(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        string? serviceName = null, brokerInstance = null, address = null, mirror = null;
        int? lifetime = null;
        bool sawService = false, sawInstance = false, sawLifetime = false;
        Span<char> upper = stackalloc char[16];
        do
        {
            context.MoveNextRequired();
            if (context.Token is not UnquotedString { Value: var option } || option.Length > upper.Length)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            RequireNextOperator(context, '=');
            context.MoveNextRequired();
            switch (upper[..option.AsSpan().ToUpperInvariant(upper)])
            {
                case "ADDRESS":
                    address = ReadStringOption(context);
                    break;
                case "BROKER_INSTANCE":
                    brokerInstance = ReadStringOption(context);
                    sawInstance = true;
                    break;
                case "LIFETIME":
                    lifetime = context.Token is Numeric number ? (int)number.Value.CoerceTo(SqlType.BigInt).AsInt64 : throw SimulatedSqlException.SyntaxErrorNear(context);
                    sawLifetime = true;
                    break;
                case "MIRROR_ADDRESS":
                    mirror = ReadStringOption(context);
                    break;
                case "SERVICE_NAME":
                    serviceName = ReadStringOption(context);
                    sawService = true;
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        } while (context.Token is Operator { Character: ',' });
        // A CREATE must name an address: without one it is Msg 102 state 22
        // at WITH (probed 2026-10-16 against SQL Server 2025).
        if (!alter && address is null)
            throw SimulatedSqlException.SyntaxErrorNear("WITH", 22);
        if (context.Batch.IsSkipping)
            return;

        var database = context.CurrentDatabase;
        var broker = database.ServiceBroker;
        if (address is not null && !IsRouteAddress(address))
            throw SimulatedSqlException.RouteAddressInvalid(name);
        BrokerRoute route;
        if (alter)
        {
            database.RejectWriteWhenReadOnly();
            if (!broker.Routes.TryGetValue(name, out route!) || !MayChangeBrokerObject(context, "ALTER ANY ROUTE", 19, route.Id, route.PrincipalId))
                throw SimulatedSqlException.BrokerObjectNotFound("alter", "route", name);
            var (oldService, oldInstance, oldLifetime, oldAddress, oldMirror) = (route.RemoteServiceName, route.BrokerInstance, route.Lifetime, route.Address, route.MirrorAddress);
            RecordDdlUndo(context, () => (route.RemoteServiceName, route.BrokerInstance, route.Lifetime, route.Address, route.MirrorAddress) = (oldService, oldInstance, oldLifetime, oldAddress, oldMirror));
        }
        else
        {
            RequireBrokerCreatePermission(context, "CREATE ROUTE", 17);
            if (broker.Routes.ContainsKey(name))
                throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 16);
            route = new BrokerRoute(broker.AllocateRouteId(), name, ownerId);
        }
        if (!alter || sawService)
            route.RemoteServiceName = serviceName;
        if (!alter || sawInstance)
            route.BrokerInstance = brokerInstance;
        if (!alter || sawLifetime)
            route.Lifetime = lifetime is { } seconds ? context.Batch.CurrentStatement.UtcNow.AddSeconds(seconds) : null;
        if (address is not null)
            route.Address = address;
        if (!alter || mirror is not null)
            route.MirrorAddress = mirror;
        if (!alter)
        {
            if (!broker.Routes.TryAdd(name, route))
                throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 16);
            RecordSlotUndo<BrokerRoute>(context, broker.Routes, name, null);
        }
        RecordDdlEvent(context, alter ? "ALTER_ROUTE" : "CREATE_ROUTE", schemaName: null, name, "ROUTE");
    }

    /// <summary>A string option's value at the cursor, left on it.</summary>
    private static string ReadStringOption(ParserContext context) =>
        context.Token is Literal { Value: { IsNull: false } value } && value.Type is VarcharSqlType or NVarcharSqlType
            ? value.AsString
            : throw SimulatedSqlException.SyntaxErrorNear(context);

    /// <summary><c>LOCAL</c>, <c>TRANSPORT</c> or a <c>TCP://host:port</c> address.</summary>
    private static bool IsRouteAddress(string address)
    {
        if (address.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) || address.Equals("TRANSPORT", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!address.StartsWith("TCP://", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = address.AsSpan(6);
        var colon = rest.LastIndexOf(':');
        return colon > 0 && int.TryParse(rest[(colon + 1)..].TrimEnd('/'), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    // ---- remote service bindings ----

    private static void CreateOrAlterRemoteServiceBinding(ParserContext context, bool alter)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        context.MoveNextRequired();
        var ownerId = alter ? 0 : ReadBrokerOwner(context);
        string? serviceName = null;
        if (!alter)
        {
            if (context.Token is not ReservedKeyword { Keyword: Keyword.To })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            RequireNextWord(context, "SERVICE");
            context.MoveNextRequired();
            serviceName = ReadStringOption(context);
            context.MoveNextRequired();
        }
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        string? userName = null;
        bool? anonymous = null;
        do
        {
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.User })
            {
                RequireNextOperator(context, '=');
                context.MoveNextRequired();
                userName = ReadBrokerName(context);
                context.MoveNextOptional();
            }
            else if (AtWord(context, "ANONYMOUS"))
            {
                anonymous = ReadOnOffAfterEquals(context);
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        } while (context.Token is Operator { Character: ',' });
        if (!alter && userName is null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.Batch.IsSkipping)
            return;

        var database = context.CurrentDatabase;
        var broker = database.ServiceBroker;
        int? remotePrincipal = null;
        if (userName is not null)
        {
            remotePrincipal = database.Principals.TryGetValue(userName, out var user)
                ? user.PrincipalId
                : throw SimulatedSqlException.CannotFindUser(userName);
        }
        if (alter)
        {
            database.RejectWriteWhenReadOnly();
            if (!broker.RemoteServiceBindings.TryGetValue(name, out var binding)
                || !MayChangeBrokerObject(context, "ALTER ANY REMOTE SERVICE BINDING", 18, binding.Id, binding.PrincipalId))
            {
                throw SimulatedSqlException.BrokerObjectNotFound("alter", "remote service binding", name);
            }
            var (oldPrincipal, oldAnonymous) = (binding.RemotePrincipalId, binding.Anonymous);
            binding.RemotePrincipalId = remotePrincipal ?? binding.RemotePrincipalId;
            binding.Anonymous = anonymous ?? binding.Anonymous;
            RecordDdlUndo(context, () => (binding.RemotePrincipalId, binding.Anonymous) = (oldPrincipal, oldAnonymous));
        }
        else
        {
            RequireBrokerCreatePermission(context, "CREATE REMOTE SERVICE BINDING", 17);
            var binding = new RemoteServiceBinding(broker.AllocateBindingId(), name, ownerId, serviceName!, remotePrincipal!.Value, anonymous ?? false);
            if (!broker.RemoteServiceBindings.TryAdd(name, binding))
                throw SimulatedSqlException.ThereIsAlreadyAnObject(name, 17);
            RecordSlotUndo<RemoteServiceBinding>(context, broker.RemoteServiceBindings, name, null);
        }
        RecordDdlEvent(context, alter ? "ALTER_REMOTE_SERVICE_BINDING" : "CREATE_REMOTE_SERVICE_BINDING", schemaName: null, name, "REMOTE SERVICE BINDING");
    }

    // ---- broker priorities ----

    private static void CreateOrAlterBrokerPriority(ParserContext context, bool alter)
    {
        context.MoveNextRequired();
        var name = ReadBrokerName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        RequireNextWord(context, "CONVERSATION");
        context.MoveNextOptional();
        // Each property: unset (not written), null (ANY) or a value.
        (bool Written, string? Value) contract = default, local = default, remote = default;
        (bool Written, int? Value) level = default;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Set })
        {
            RequireNextOperator(context, '(');
            Span<char> upper = stackalloc char[20];
            do
            {
                context.MoveNextRequired();
                if (context.Token is not UnquotedString { Value: var property } || property.Length > upper.Length)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                RequireNextOperator(context, '=');
                context.MoveNextRequired();
                switch (upper[..property.AsSpan().ToUpperInvariant(upper)])
                {
                    case "CONTRACT_NAME":
                        contract = (true, context.Token is ReservedKeyword { Keyword: Keyword.Any } ? null : ReadBrokerName(context));
                        break;
                    case "LOCAL_SERVICE_NAME":
                        local = (true, context.Token is ReservedKeyword { Keyword: Keyword.Any } ? null : ReadBrokerName(context));
                        break;
                    case "PRIORITY_LEVEL":
                        level = (true, context.Token switch
                        {
                            ReservedKeyword { Keyword: Keyword.Default } => null,
                            Numeric number => (int)number.Value.CoerceTo(SqlType.BigInt).AsInt64,
                            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                        });
                        break;
                    case "REMOTE_SERVICE_NAME":
                        remote = (true, context.Token is ReservedKeyword { Keyword: Keyword.Any } ? null : ReadStringOption(context));
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        else if (alter)
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.Batch.IsSkipping)
            return;

        var database = context.CurrentDatabase;
        var broker = database.ServiceBroker;
        if (level.Value is { } written && written is < 1 or > 10)
            throw SimulatedSqlException.PriorityLevelInvalid(name);
        int? contractId = null, localId = null;
        if (contract.Value is { } contractName)
        {
            contractId = broker.Contracts.TryGetValue(contractName, out var found)
                ? found.Id
                : throw SimulatedSqlException.BrokerObjectNotFound("find", "contract", contractName, 13);
        }
        if (local.Value is { } localName)
        {
            localId = broker.Services.TryGetValue(localName, out var found)
                ? found.Id
                : throw SimulatedSqlException.BrokerObjectNotFound("find", "service", localName, 13);
        }

        BrokerPriority priority;
        if (alter)
        {
            database.RejectWriteWhenReadOnly();
            if (!broker.Priorities.TryGetValue(name, out priority!) || !PermissionEnforcement.HoldsDatabasePermission(context.Batch, database, "ALTER"))
                throw SimulatedSqlException.BrokerObjectNotFound("alter", "broker priority", name);
            var (oldContract, oldLocal, oldRemote, oldLevel) = (priority.ContractId, priority.LocalServiceId, priority.RemoteServiceName, priority.Level);
            RecordDdlUndo(context, () => (priority.ContractId, priority.LocalServiceId, priority.RemoteServiceName, priority.Level) = (oldContract, oldLocal, oldRemote, oldLevel));
        }
        else
        {
            database.RejectWriteWhenReadOnly();
            if (!PermissionEnforcement.HoldsDatabasePermission(context.Batch, database, "ALTER"))
                throw SimulatedSqlException.BrokerCreatePermissionDenied("ALTER", database.Name, 17);
            priority = new BrokerPriority(broker.AllocatePriorityId(), name);
        }
        if (!alter || contract.Written)
            priority.ContractId = contractId;
        if (!alter || local.Written)
            priority.LocalServiceId = localId;
        if (!alter || remote.Written)
            priority.RemoteServiceName = remote.Value;
        if (!alter || level.Written)
            priority.Level = (byte)(level.Value ?? 5);
        foreach (var (_, other) in broker.Priorities)
        {
            if (!ReferenceEquals(other, priority)
                && (database.Collation.Equals(other.Name, name)
                    || (other.ContractId == priority.ContractId && other.LocalServiceId == priority.LocalServiceId
                        && string.Equals(other.RemoteServiceName, priority.RemoteServiceName, StringComparison.Ordinal))))
            {
                throw SimulatedSqlException.PriorityAlreadyExists(name, contract.Value, local.Value, remote.Value);
            }
        }
        if (!alter)
        {
            if (!broker.Priorities.TryAdd(name, priority))
                throw SimulatedSqlException.PriorityAlreadyExists(name, contract.Value, local.Value, remote.Value);
            RecordSlotUndo<BrokerPriority>(context, broker.Priorities, name, null);
        }
        RecordDdlEvent(context, alter ? "ALTER_BROKER_PRIORITY" : "CREATE_BROKER_PRIORITY", schemaName: null, name, "BROKER PRIORITY");
    }

    // ---- DROP ----

    /// <summary>
    /// Parses <c>DROP { MESSAGE TYPE | CONTRACT | QUEUE | SERVICE | ROUTE |
    /// REMOTE SERVICE BINDING | BROKER PRIORITY } name</c>, which take neither
    /// <c>IF EXISTS</c> nor a list (Msg 156 at <c>IF</c>).
    /// </summary>
    private static void DropBrokerObject(ParserContext context, BrokerObjectKind kind)
    {
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword keyword)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);
        MultiPartName queueName = default;
        string name;
        if (kind == BrokerObjectKind.Queue)
        {
            queueName = BatchContext.ParseObjectName(context);
            name = queueName.ToString();
        }
        else
        {
            name = ReadBrokerName(context);
        }
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return;

        var batch = context.Batch;
        var database = context.CurrentDatabase;
        var broker = database.ServiceBroker;
        switch (kind)
        {
            case BrokerObjectKind.MessageType:
                {
                    if (!broker.MessageTypes.TryGetValue(name, out var type)
                        || !MayChangeBrokerObject(context, "ALTER ANY MESSAGE TYPE", 15, type.Id, type.PrincipalId))
                    {
                        throw SimulatedSqlException.BrokerObjectNotFound("drop", "message type", name);
                    }
                    if (type.IsSystem)
                        throw SimulatedSqlException.SystemBrokerObjectCannotBeModified(9);
                    foreach (var (_, contract) in broker.Contracts)
                    {
                        if (contract.TryFindUsage(type, out _))
                            throw SimulatedSqlException.BrokerObjectBound("message type", name, "contract", 10);
                    }
                    database.RejectWriteWhenReadOnly();
                    _ = broker.MessageTypes.TryRemove(name, out _);
                    RecordSlotUndo(context, broker.MessageTypes, name, type);
                    DropSecurablePermissions(context, database, 15, type.Id);
                    RecordDdlEvent(context, "DROP_MESSAGE_TYPE", schemaName: null, name, "MESSAGE TYPE");
                    break;
                }
            case BrokerObjectKind.Contract:
                {
                    if (!broker.Contracts.TryGetValue(name, out var contract)
                        || !MayChangeBrokerObject(context, "ALTER ANY CONTRACT", 16, contract.Id, contract.PrincipalId))
                    {
                        throw SimulatedSqlException.BrokerObjectNotFound("drop", "contract", name);
                    }
                    if (contract.IsSystem)
                        throw SimulatedSqlException.SystemBrokerObjectCannotBeModified(8);
                    foreach (var (_, service) in broker.Services)
                    {
                        if (Array.IndexOf(service.Contracts, contract) >= 0)
                            throw SimulatedSqlException.BrokerObjectBound("contract", name, "service", 11);
                    }
                    database.RejectWriteWhenReadOnly();
                    _ = broker.Contracts.TryRemove(name, out _);
                    RecordSlotUndo(context, broker.Contracts, name, contract);
                    DropSecurablePermissions(context, database, 16, contract.Id);
                    RecordDdlEvent(context, "DROP_CONTRACT", schemaName: null, name, "CONTRACT");
                    break;
                }
            case BrokerObjectKind.Queue:
                {
                    // A shipped queue is always bound to its service.
                    if (batch.TryResolveQueue(queueName, out var shipped) && shipped.IsMsShipped)
                        throw SimulatedSqlException.BrokerObjectBound("queue", name, "service", 14);
                    if (!batch.TryResolveSchema(queueName, out var schema) || !schema.Queues.TryGetValue(queueName.Leaf, out var queue))
                    {
                        if (batch.TryResolveSchema(queueName, out var otherSchema))
                            RejectDropOfOtherKind(otherSchema, queueName, "QUEUE");
                        throw SimulatedSqlException.BrokerObjectNotFound("drop", "queue", name);
                    }
                    if (!PermissionEnforcement.HasDropAuthority(batch, schema, queue.ObjectId))
                        throw SimulatedSqlException.BrokerObjectNotFound("drop", "queue", name);
                    if (broker.QueueHasService(queue))
                        throw SimulatedSqlException.BrokerObjectBound("queue", name, "service", 14);
                    schema.Database.RejectWriteWhenReadOnly();
                    batch.LockDefinitionName(schema, queue.Name);
                    batch.LockDefinition(schema, queue);
                    _ = schema.Queues.TryRemove(queue.Name, out _);
                    RecordSlotUndo(context, schema.Queues, queue.Name, queue);
                    DropSecurablePermissions(context, schema.Database, PermissionChecker.ClassObject, queue.ObjectId);
                    RecordDdlEvent(context, "DROP_QUEUE", schema.Name, queue.Name, "QUEUE");
                    break;
                }
            case BrokerObjectKind.Service:
                {
                    if (!broker.Services.TryGetValue(name, out var service)
                        || !MayChangeBrokerObject(context, "ALTER ANY SERVICE", 17, service.Id, service.PrincipalId))
                    {
                        throw SimulatedSqlException.BrokerObjectNotFound("drop", "service", name);
                    }
                    if (service.IsSystem)
                        throw SimulatedSqlException.SystemBrokerObjectCannotBeModified(7);
                    database.RejectWriteWhenReadOnly();
                    _ = broker.Services.TryRemove(name, out _);
                    RecordSlotUndo(context, broker.Services, name, service);
                    DropSecurablePermissions(context, database, 17, service.Id);
                    DropServiceConversations(context, service);
                    RecordDdlEvent(context, "DROP_SERVICE", schemaName: null, name, "SERVICE");
                    break;
                }
            case BrokerObjectKind.Route:
                {
                    if (!broker.Routes.TryGetValue(name, out var route)
                        || !MayChangeBrokerObject(context, "ALTER ANY ROUTE", 19, route.Id, route.PrincipalId))
                    {
                        throw SimulatedSqlException.BrokerObjectNotFound("drop", "route", name);
                    }
                    database.RejectWriteWhenReadOnly();
                    _ = broker.Routes.TryRemove(name, out _);
                    RecordSlotUndo(context, broker.Routes, name, route);
                    DropSecurablePermissions(context, database, 19, route.Id);
                    RecordDdlEvent(context, "DROP_ROUTE", schemaName: null, name, "ROUTE");
                    break;
                }
            case BrokerObjectKind.RemoteServiceBinding:
                {
                    if (!broker.RemoteServiceBindings.TryGetValue(name, out var binding)
                        || !MayChangeBrokerObject(context, "ALTER ANY REMOTE SERVICE BINDING", 18, binding.Id, binding.PrincipalId))
                    {
                        throw SimulatedSqlException.BrokerObjectNotFound("drop", "remote service binding", name);
                    }
                    database.RejectWriteWhenReadOnly();
                    _ = broker.RemoteServiceBindings.TryRemove(name, out _);
                    RecordSlotUndo(context, broker.RemoteServiceBindings, name, binding);
                    DropSecurablePermissions(context, database, 18, binding.Id);
                    RecordDdlEvent(context, "DROP_REMOTE_SERVICE_BINDING", schemaName: null, name, "REMOTE SERVICE BINDING");
                    break;
                }
            default:
                {
                    if (!broker.Priorities.TryGetValue(name, out var priority) || !PermissionEnforcement.HoldsDatabasePermission(batch, database, "ALTER"))
                        throw SimulatedSqlException.BrokerObjectNotFound("drop", "broker priority", name);
                    database.RejectWriteWhenReadOnly();
                    _ = broker.Priorities.TryRemove(name, out _);
                    RecordSlotUndo(context, broker.Priorities, name, priority);
                    RecordDdlEvent(context, "DROP_BROKER_PRIORITY", schemaName: null, name, "BROKER PRIORITY");
                    break;
                }
        }
    }
}
