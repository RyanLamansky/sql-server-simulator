using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The Service Broker conversation statements — BEGIN DIALOG, SEND, RECEIVE,
// GET CONVERSATION GROUP, MOVE CONVERSATION, BEGIN CONVERSATION TIMER and END
// CONVERSATION, the WAITFOR forms of RECEIVE and GET — and the delivery of a
// sent message to the queue its far service reads. Behavior probed 2026-10-16
// against SQL Server 2025; see docs/claude/service-broker.md.
partial class Simulation
{
    private BrokerRuntime? broker;

    /// <summary>The conversation state, created on the first conversation statement.</summary>
    internal BrokerRuntime Broker
    {
        get
        {
            if (Volatile.Read(ref this.broker) is { } existing)
                return existing;
            var created = new BrokerRuntime(this);
            return Interlocked.CompareExchange(ref this.broker, created, null) ?? created;
        }
    }

    /// <summary>The conversation state when a statement has made any, else null.</summary>
    internal BrokerRuntime? BrokerIfUsed => Volatile.Read(ref this.broker);

    /// <summary>The conversation statement the tokens at the cursor open.</summary>
    private enum BrokerStatement
    {
        None,
        BeginDialog,
        BeginTimer,
        EndConversation,
        Send,
        Receive,
        GetGroup,
        MoveConversation,
        WaitForReceive,
        WaitForGetGroup,
    }

    /// <summary>
    /// The conversation statement at the cursor, read off its first words
    /// without moving the cursor; <see cref="BrokerStatement.None"/> for any
    /// other statement.
    /// </summary>
    private static BrokerStatement BrokerStatementAt(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            var first = context.Token;
            var second = context.GetNextOptional();
            return first switch
            {
                ReservedKeyword { Keyword: Keyword.Begin } when IsKeyWord(second, "DIALOG") => BrokerStatement.BeginDialog,
                ReservedKeyword { Keyword: Keyword.Begin } when IsKeyWord(second, "CONVERSATION") && IsKeyWord(context.GetNextOptional(), "TIMER") => BrokerStatement.BeginTimer,
                ReservedKeyword { Keyword: Keyword.End } when IsKeyWord(second, "CONVERSATION") => BrokerStatement.EndConversation,
                UnquotedString { Value: var word } when BuiltInToken.Equals(word, "SEND") && second is ReservedKeyword { Keyword: Keyword.On } => BrokerStatement.Send,
                UnquotedString { Value: var word } when BuiltInToken.Equals(word, "RECEIVE") => BrokerStatement.Receive,
                UnquotedString { Value: var word } when BuiltInToken.Equals(word, "GET") && IsKeyWord(second, "CONVERSATION") => BrokerStatement.GetGroup,
                UnquotedString { Value: var word } when BuiltInToken.Equals(word, "MOVE") && IsKeyWord(second, "CONVERSATION") => BrokerStatement.MoveConversation,
                ReservedKeyword { Keyword: Keyword.WaitFor } when second is Operator { Character: '(' } => context.GetNextOptional() switch
                {
                    UnquotedString { Value: var word } when BuiltInToken.Equals(word, "RECEIVE") => BrokerStatement.WaitForReceive,
                    UnquotedString { Value: var word } when BuiltInToken.Equals(word, "GET") => BrokerStatement.WaitForGetGroup,
                    _ => BrokerStatement.None,
                },
                _ => BrokerStatement.None,
            };
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Whether the <c>BEGIN</c> or <c>END</c> at the cursor opens a
    /// conversation statement rather than a block — what the block scanners
    /// pass over.
    /// </summary>
    internal static bool IsBrokerBeginOrEnd(ParserContext context) =>
        BrokerStatementAt(context) is BrokerStatement.BeginDialog or BrokerStatement.BeginTimer or BrokerStatement.EndConversation;

    /// <summary>Runs the conversation statement at the cursor within the statement's atomic scope.</summary>
    private static SimulatedStatementOutcome RunBrokerStatement(ParserContext context, BrokerStatement statement) =>
        RunMutation(context, context =>
        {
            context.Batch.CurrentStatement.CountsNoRows = statement is not (BrokerStatement.Receive or BrokerStatement.WaitForReceive);
            return RunBrokerStatementBody(context, statement);
        });

    private static SimulatedStatementOutcome RunBrokerStatementBody(ParserContext context, BrokerStatement statement) =>
        statement switch
        {
            BrokerStatement.BeginDialog => ParseBeginDialog(context),
            BrokerStatement.BeginTimer => ParseBeginConversationTimer(context),
            BrokerStatement.EndConversation => ParseEndConversation(context),
            BrokerStatement.Send => ParseSend(context),
            BrokerStatement.Receive => ParseReceive(context, waits: false),
            BrokerStatement.GetGroup => ParseGetConversationGroup(context, waits: false),
            BrokerStatement.MoveConversation => ParseMoveConversation(context),
            BrokerStatement.WaitForReceive => ParseReceive(context, waits: true),
            _ => ParseGetConversationGroup(context, waits: true),
        };

    private static SimulatedNonQuery NoBrokerCount => new(0, countsRowsReturned: true);

    // ---- shared pieces ----

    /// <summary>
    /// Parses a conversation handle or group id operand — a variable or
    /// literal — refusing a subquery as real does (Msg 1046), and requiring
    /// a <c>uniqueidentifier</c>-compatible type (Msg 206).
    /// </summary>
    private static Expression ParseBrokerOperand(ParserContext context)
    {
        if (context.Token is Operator { Character: '(' } && PeekAfterLead(context) is ReservedKeyword { Keyword: Keyword.Select })
            throw SimulatedSqlException.SubqueriesNotAllowedInThisContext();
        var operand = Expression.Parse(context);
        var type = operand.GetSqlType(context.Batch, static name => throw SimulatedSqlException.InvalidColumnName(name));
        AssignmentRules.RequireAssignable(operand, type, SqlType.UniqueIdentifier);
        return operand;
    }

    /// <summary>The value of a handle or group operand, NULL when it is.</summary>
    private static Guid? EvaluateGuid(BatchContext batch, Expression operand)
    {
        var value = operand.Run(new RuntimeContext(NoColumnResolver, batch));
        return value.IsNull ? null : value.CoerceTo(SqlType.UniqueIdentifier).AsGuid;
    }

    /// <summary>Reads the <c>@variable</c> at the cursor that a statement assigns, typed to take a <c>uniqueidentifier</c>.</summary>
    private static string ParseBrokerOutputVariable(ParserContext context)
    {
        if (context.Token is not AtPrefixedString variable)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var slot = context.Batch.GetVariableSlot(variable.Value);
        AssignmentRules.RequireAssignable(SqlType.UniqueIdentifier, slot.DeclaredType);
        context.MoveNextRequired();
        return variable.Value;
    }

    private static void AssignBrokerVariable(BatchContext batch, string name, SqlValue value)
    {
        var slot = batch.GetVariableSlot(name);
        slot.Assign(Cast.ApplyCoercion(value, slot.DeclaredType, slot.DeclaredMaxLength));
    }

    // ---- BEGIN DIALOG ----

    /// <summary>
    /// Parses and runs <c>BEGIN DIALOG [CONVERSATION] @h FROM SERVICE s TO
    /// SERVICE 'name' [, 'instance' | 'CURRENT DATABASE'] [ON CONTRACT c]
    /// [WITH {RELATED_CONVERSATION = @h | RELATED_CONVERSATION_GROUP = @g}
    /// [,] [LIFETIME = n] [,] [ENCRYPTION = ON | OFF]]</c>, creating the
    /// initiator's endpoint in <c>STARTED_OUTBOUND</c>; the target's waits for
    /// the first message.
    /// </summary>
    private static SimulatedNonQuery ParseBeginDialog(ParserContext context)
    {
        context.MoveNextRequired(); // DIALOG
        if (IsKeyWord(context.GetNextRequired(), "CONVERSATION"))
            context.MoveNextRequired();
        var handleVariable = ParseBrokerOutputVariable(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.From })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        RequireNextWord(context, "SERVICE");
        context.MoveNextRequired();
        var fromName = ReadBrokerName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.To })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        RequireNextWord(context, "SERVICE");
        context.MoveNextRequired();
        // The far service and broker instance are a string or a variable,
        // nothing else (probed 2026-10-16 against SQL Server 2025: NULL is
        // Msg 156).
        var toService = ParseServiceNameOperand(context);
        Expression? instance = null;
        if (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            instance = ParseServiceNameOperand(context);
        }
        string? contractName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            RequireNextWord(context, "CONTRACT");
            context.MoveNextRequired();
            contractName = ReadBrokerName(context);
            context.MoveNextOptional();
        }
        Expression? relatedConversation = null, relatedGroup = null, lifetime = null;
        bool? encryption = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            do
            {
                context.MoveNextRequired();
                if (AtWord(context, "RELATED_CONVERSATION"))
                {
                    RequireNextOperator(context, '=');
                    context.MoveNextRequired();
                    relatedConversation = ParseBrokerOperand(context);
                }
                else if (AtWord(context, "RELATED_CONVERSATION_GROUP"))
                {
                    RequireNextOperator(context, '=');
                    context.MoveNextRequired();
                    relatedGroup = ParseBrokerOperand(context);
                }
                else if (AtWord(context, "LIFETIME"))
                {
                    RequireNextOperator(context, '=');
                    context.MoveNextRequired();
                    lifetime = Expression.Parse(context);
                }
                else if (AtWord(context, "ENCRYPTION"))
                {
                    encryption = ReadOnOffAfterEquals(context);
                }
                else
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
            } while (context.Token is Operator { Character: ',' });
        }
        var batch = context.Batch;
        if (batch.IsSkipping)
            return NoBrokerCount;

        var database = batch.CurrentDatabase;
        var objects = database.ServiceBroker;
        if (!objects.Services.TryGetValue(fromName, out var fromService))
            throw SimulatedSqlException.InvalidObjectName(new MultiPartName(fromName), 72);
        var contract = objects.DefaultContract;
        if (contractName is not null && !objects.Contracts.TryGetValue(contractName, out contract!))
            throw SimulatedSqlException.InvalidObjectName(new MultiPartName(contractName), 73);
        RequireQueueReceive(batch, fromService.Queue, 41);
        var runtime = new RuntimeContext(NoColumnResolver, batch);
        var targetValue = toService.Run(runtime);
        if (targetValue.IsNull)
            throw SimulatedSqlException.BrokerTargetServiceMissing();
        var target = targetValue.CoerceTo(SqlType.NVarchar).AsString;
        var seconds = lifetime is null ? int.MaxValue : lifetime.Run(runtime) switch
        {
            { IsNull: true } => int.MaxValue,
            var value => value.CoerceTo(SqlType.BigInt).AsInt64 is var asLong and >= 1 and <= int.MaxValue
                ? (int)asLong
                : throw SimulatedSqlException.DialogLifetimeOutOfRange(value.CoerceTo(SqlType.BigInt).AsInt64),
        };
        _ = instance?.Run(runtime);
        var relatedHandle = relatedConversation is null ? default(Guid?) : EvaluateGuid(batch, relatedConversation) ?? throw SimulatedSqlException.ConversationHandleMissing(6);
        var relatedGroupId = relatedGroup is null ? default : EvaluateGuid(batch, relatedGroup);

        var simulation = batch.Connection.Simulation;
        var broker = simulation.Broker;
        var owner = batch.Connection.LockOwner;
        var now = batch.CurrentStatement.UtcNow;
        ConversationEndpoint endpoint;
        lock (broker.Gate)
        {
            broker.FireDue(now);
            ConversationGroup? group = null;
            if (relatedHandle is { } related)
            {
                group = (broker.Find(database, related, owner) ?? throw SimulatedSqlException.ConversationHandleNotFound(related, 6)).Group;
            }
            else if (relatedGroupId is { } groupId && broker.Groups.TryGetValue(new(database, groupId), out var existing) && BrokerRuntime.Sees(existing.PendingOwner, owner))
            {
                group = existing;
            }
            if (group is null)
            {
                group = new ConversationGroup(relatedGroupId ?? broker.NewId(), database, fromService) { PendingOwner = BrokerRuntime.PendingOwnerFor(batch) };
                broker.Groups[group.Key] = group;
                var created = group;
                broker.Log(batch, () => _ = broker.Groups.Remove(created.Key), () => created.PendingOwner = null);
            }
            _ = broker.LockGroup(batch, group, skipWhenHeld: false);
            endpoint = new ConversationEndpoint(broker.NewId(), Guid.NewGuid(), isInitiator: true, database, fromService, contract, group, target,
                batch.Connection.Security.Effective.DatabasePrincipalId, AddSecondsCapped(now, seconds), encryption ?? true)
            {
                PendingOwner = BrokerRuntime.PendingOwnerFor(batch),
            };
            broker.Endpoints[endpoint.Key] = endpoint;
            var added = endpoint;
            broker.Log(batch, () => _ = broker.Endpoints.Remove(added.Key), () => added.PendingOwner = null);
        }
        AssignBrokerVariable(batch, handleVariable, SqlValue.FromGuid(endpoint.Handle));
        return NoBrokerCount;
    }

    private static Expression ParseServiceNameOperand(ParserContext context) => context.Token switch
    {
        Literal { Value.IsNull: false } or AtPrefixedString => Expression.Parse(context),
        ReservedKeyword keyword => throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// Ends the conversations of a service <c>DROP SERVICE</c> removes: each
    /// of its endpoints closes with its messages, and the far side of each
    /// gets Msg -8470's Error (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static void DropServiceConversations(ParserContext context, BrokerService service)
    {
        var batch = context.Batch;
        if (batch.Connection.Simulation.BrokerIfUsed is not { } broker)
            return;
        var now = batch.CurrentStatement.UtcNow;
        lock (broker.Gate)
        {
            foreach (var endpoint in broker.Endpoints.Values.Where(endpoint => ReferenceEquals(endpoint.Service, service)).ToList())
            {
                if (endpoint.Far is { State: EndpointState.StartedOutbound or EndpointState.Conversing or EndpointState.DisconnectedInbound } far)
                    ErrorEndpoint(batch, broker, far, -8470, "Remote service has been dropped.", now);
                CloseEndpoint(batch, broker, endpoint, removes: false);
            }
        }
    }

    private static DateTime AddSecondsCapped(DateTime now, int seconds) =>
        now.AddSeconds(Math.Min(seconds, (DateTime.MaxValue - now).TotalSeconds - 1));

    /// <summary>
    /// Requires <c>RECEIVE</c> on <paramref name="queue"/> — Msg 1088 at
    /// <paramref name="state"/> without it (41 for <c>BEGIN DIALOG</c>'s
    /// initiator queue, 42 for a <c>RECEIVE</c>).
    /// </summary>
    private static void RequireQueueReceive(BatchContext batch, ServiceQueue queue, byte state)
    {
        if (!PermissionEnforcement.HoldsPermission(batch, queue.Schema.Database, Permission.Receive, PermissionChecker.ClassObject, queue.ObjectId, queue.SchemaId))
            throw SimulatedSqlException.QueueNotFoundOrDenied(queue.Name, state);
    }

    // ---- SEND ----

    /// <summary>
    /// Parses and runs <c>SEND ON CONVERSATION {@h | (@h [, …])} [MESSAGE TYPE
    /// t] [(body)]</c>: one message, its body converted to <c>varbinary(max)</c>
    /// (an <c>xml</c> one as its UTF-16 text), sent on each conversation.
    /// </summary>
    private static SimulatedNonQuery ParseSend(ParserContext context)
    {
        context.MoveNextRequired(); // ON
        RequireNextWord(context, "CONVERSATION");
        context.MoveNextRequired();
        var handles = new List<Expression>();
        if (context.Token is Operator { Character: '(' } && PeekAfterLead(context) is not ReservedKeyword { Keyword: Keyword.Select })
        {
            do
            {
                context.MoveNextRequired();
                handles.Add(ParseBrokerOperand(context));
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        else
        {
            handles.Add(ParseBrokerOperand(context));
        }
        string? typeName = null;
        if (AtWord(context, "MESSAGE"))
        {
            RequireNextWord(context, "TYPE");
            context.MoveNextRequired();
            typeName = ReadBrokerName(context);
            context.MoveNextOptional();
        }
        Expression? body = null;
        if (context.Token is Operator { Character: '(' })
        {
            context.MoveNextRequired();
            body = Expression.Parse(context);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        var batch = context.Batch;
        if (batch.IsSkipping)
            return NoBrokerCount;

        var database = batch.CurrentDatabase;
        var objects = database.ServiceBroker;
        var type = objects.DefaultContract.Usages[0].MessageType;
        if (typeName is not null && !objects.MessageTypes.TryGetValue(typeName, out type!))
            throw SimulatedSqlException.InvalidObjectName(new MultiPartName(typeName), 74);
        var runtime = new RuntimeContext(NoColumnResolver, batch);
        byte[]? bytes = null;
        if (body is not null)
        {
            var value = body.Run(runtime);
            if (value.IsNull)
                throw SimulatedSqlException.MessageBodyNull();
            bytes = MessageBodyBytes(value);
        }
        var ids = new List<Guid>(handles.Count);
        foreach (var handle in handles)
            ids.Add(EvaluateGuid(batch, handle) ?? throw SimulatedSqlException.ConversationHandleMissing());

        var simulation = batch.Connection.Simulation;
        var broker = simulation.Broker;
        var owner = batch.Connection.LockOwner;
        var now = batch.CurrentStatement.UtcNow;
        lock (broker.Gate)
        {
            broker.FireDue(now);
            foreach (var id in ids)
            {
                var endpoint = broker.Find(database, id, owner) ?? throw SimulatedSqlException.ConversationHandleNotFound(id, 4);
                if (endpoint.State is not (EndpointState.StartedOutbound or EndpointState.Conversing))
                    throw SimulatedSqlException.EndpointStateRefusesSend(endpoint.StateCode);
                if (!endpoint.Contract.TryFindUsage(type, out var usage))
                    throw SimulatedSqlException.MessageTypeNotInContract(type.Name);
                if (endpoint.IsInitiator ? !usage.SentByInitiator : !usage.SentByTarget)
                    throw SimulatedSqlException.MessageTypeWrongSide(type.Name, endpoint.IsInitiator);
                _ = broker.LockGroup(batch, endpoint.Group, skipWhenHeld: false);
                var sequence = endpoint.SendSequence;
                SetSendSequence(batch, broker, endpoint, sequence + 1);
                simulation.Deliver(batch, broker, endpoint, type, sequence, bytes, isError: false, isEnd: false, now);
                // An initiator's first message moves it on, delivered or held.
                if (endpoint.State == EndpointState.StartedOutbound)
                    SetState(batch, broker, endpoint, EndpointState.Conversing);
            }
        }
        return NoBrokerCount;
    }

    /// <summary>
    /// A message body as <c>SEND</c> stores it: the value converted to
    /// <c>varbinary(max)</c>, an <c>xml</c> one as its text in UTF-16 behind a
    /// byte order mark, and an empty one as no body at all (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    private static byte[]? MessageBodyBytes(SqlValue value)
    {
        var bytes = value.Type is XmlSqlType
            ? [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(value.AsString)]
            : value.CoerceTo(SqlType.VarbinaryMax).AsBytes;
        return bytes.Length == 0 ? null : bytes;
    }

    private static void SetSendSequence(BatchContext batch, BrokerRuntime broker, ConversationEndpoint endpoint, long next)
    {
        var previous = endpoint.SendSequence;
        endpoint.SendSequence = next;
        broker.Log(batch, () => endpoint.SendSequence = previous, null);
    }

    /// <summary>
    /// Delivers one message <paramref name="from"/> sends to the queue of its
    /// far service, in the sending transaction — or, where real's broker
    /// can't, holds it on <c>sys.transmission_queue</c> with the status real
    /// reports, or errors the conversation with the Error message real sends
    /// (probed 2026-10-16 against SQL Server 2025). The first message an
    /// initiator sends routes the dialog and creates the target's endpoint.
    /// </summary>
    private void Deliver(BatchContext batch, BrokerRuntime broker, ConversationEndpoint from, BrokerMessageType type, long sequence, byte[]? body, bool isError, bool isEnd, DateTime now)
    {
        if (from.HeldStatus is { } held)
        {
            Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, held);
            return;
        }
        if (!from.Database.BrokerEnabled)
        {
            Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, BrokerDisabledStatus);
            return;
        }
        if (from.Far is not { } far)
        {
            if (this.RouteToLocalService(from, out var status) is not { } service)
            {
                Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, status);
                return;
            }
            // A service in another database takes the message as the sending
            // database's owner, a context only a TRUSTWORTHY database's carries
            // there (probed 2026-10-16 against SQL Server 2025).
            if (!ReferenceEquals(service.Queue.Schema.Database, from.Database) && !from.Database.Trustworthy)
            {
                Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, UntrustedSenderStatus(from.Database, service.Queue.Schema.Database));
                return;
            }
            var targetObjects = service.Queue.Schema.Database.ServiceBroker;
            if (!targetObjects.Contracts.TryGetValue(from.Contract.Name, out var targetContract) || Array.IndexOf(service.Contracts, targetContract) < 0)
            {
                ErrorEndpoint(batch, broker, from, -8408, $"Target service '{service.Name}' does not support contract '{from.Contract.Name}'.", now);
                Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, HeldAfterError);
                return;
            }
            if (!service.Queue.Settings.Enabled)
            {
                Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, QueueRefusedStatus);
                return;
            }
            var targetType = targetObjects.MessageTypes.GetValueOrDefault(type.Name) ?? type;
            if (ValidationFailure(targetType, body, from, sequence, firstMessage: true) is { } failure)
            {
                ErrorEndpoint(batch, broker, from, failure.Code, failure.Description, now);
                Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, HeldAfterError);
                return;
            }
            far = CreateTargetEndpoint(batch, broker, from, service, targetContract, now);
            type = targetType;
        }
        else
        {
            if (!far.Service.Queue.Settings.Enabled)
            {
                Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, QueueRefusedStatus);
                return;
            }
            var farType = far.Database.ServiceBroker.MessageTypes.GetValueOrDefault(type.Name) ?? type;
            if (!isError && !isEnd && ValidationFailure(farType, body, from, sequence, firstMessage: false) is { } failure)
            {
                ErrorEndpoint(batch, broker, far, failure.Code, failure.Description, now);
                ErrorEndpoint(batch, broker, from, -8462, "The remote conversation endpoint is either in a state where no more messages can be exchanged, or it has been dropped.", now);
                Hold(batch, broker, from, type, sequence, body, isError, isEnd, now, HeldAfterError);
                return;
            }
            type = farType;
        }

        _ = broker.LockGroup(batch, far.Group, skipWhenHeld: false);
        _ = broker.Enqueue(batch, far, type, sequence, body, now);
        var received = far.ReceiveSequence;
        far.ReceiveSequence = received + 1;
        broker.Log(batch, () => far.ReceiveSequence = received, null);
        if (isEnd || isError)
            SetState(batch, broker, far, isError ? EndpointState.Error : EndpointState.DisconnectedInbound);
        if (!isError && !isEnd && from.Service.Queue.Settings.Retention)
        {
            var retained = broker.Enqueue(batch, from, type, sequence, body, now);
            retained.Status = 3;
        }
    }

    /// <summary>The start of what <c>sys.transmission_queue</c> reports for a message an untrusted database sent to another.</summary>
    private const string UntrustedSenderPrefix = "An exception occurred while enqueueing a message in the target queue. Error: 916, State: 3.";

    /// <summary>What <c>sys.transmission_queue</c> reports for a message <paramref name="sender"/>, not TRUSTWORTHY, sent to a service in <paramref name="target"/>.</summary>
    private static string UntrustedSenderStatus(Database sender, Database target) =>
        $"{UntrustedSenderPrefix} The server principal \"{sender.OwnerLoginName}\" is not able to access the database \"{target.Name}\" under the current security context.";

    /// <summary>Delivers the messages <paramref name="database"/> held because it wasn't TRUSTWORTHY, once it is.</summary>
    internal void RedeliverUntrusted(BatchContext batch, Database database) =>
        this.RedeliverHeld(batch, database, null, static status => status.StartsWith(UntrustedSenderPrefix, StringComparison.Ordinal));

    /// <summary>What <c>sys.transmission_queue</c> reports for a message its database's disabled broker holds.</summary>
    private const string BrokerDisabledStatus = "The broker is disabled in the sender's database.";

    /// <summary>
    /// Delivers again the messages <paramref name="database"/>'s endpoints
    /// hold for <paramref name="status"/> once its cause is gone — a broker
    /// enabled, a queue turned back on — as real's broker retries them.
    /// </summary>
    internal void RedeliverHeld(BatchContext batch, Database? database, string status) => this.RedeliverHeld(batch, database, status, null);

    private void RedeliverHeld(BatchContext batch, Database? database, string? status, Func<string, bool>? matches)
    {
        if (this.BrokerIfUsed is not { } broker)
            return;
        var now = batch.CurrentStatement.UtcNow;
        lock (broker.Gate)
        {
            var held = broker.Transmission.FindAll(message => (database is null || ReferenceEquals(message.From.Database, database)) && message.PendingOwner is null
                && (matches is null ? message.Status == status : matches(message.Status)));
            foreach (var message in held)
            {
                _ = broker.Transmission.Remove(message);
                var from = message.From;
                var type = from.Database.ServiceBroker.MessageTypes.GetValueOrDefault(message.MessageTypeName);
                if (type is not null)
                    this.Deliver(batch, broker, from, type, message.Sequence, message.Body, message.IsError, message.IsEndOfDialog, now);
            }
            Monitor.PulseAll(broker.Gate);
        }
    }

    /// <summary>What <c>sys.transmission_queue</c> reports for a message held after an Error reached its conversation.</summary>
    private const string HeldAfterError = "Service Broker received an error message on this conversation. Service Broker will not transmit the message; it will be held until the application ends the conversation.";

    /// <summary>What <c>sys.transmission_queue</c> reports for a message a disabled queue refused.</summary>
    private const string QueueRefusedStatus = "One or more messages could not be delivered to the local service targeted by this dialog.";

    /// <summary>
    /// The local service an initiator's dialog reaches through its database's
    /// routes: a route naming the service wins over one naming none, and a
    /// route whose address isn't <c>LOCAL</c> leaves the message waiting —
    /// for the session keys a database without a master key can't make, as
    /// real's local broker reports even under <c>ENCRYPTION = OFF</c>. A
    /// <c>LOCAL</c> route finds the service in the initiator's database, else
    /// in exactly one other database of the instance.
    /// </summary>
    private BrokerService? RouteToLocalService(ConversationEndpoint from, out string status)
    {
        BrokerRoute? named = null, general = null;
        foreach (var (_, route) in from.Database.ServiceBroker.Routes)
        {
            if (route.RemoteServiceName is null)
                general ??= route;
            else if (string.Equals(route.RemoteServiceName, from.FarService, StringComparison.Ordinal))
                named ??= route;
        }
        var chosen = named ?? general;
        const string noKeys = "The session keys for this conversation could not be created or accessed. The database master key is required for this operation.";
        if (chosen is null)
        {
            status = "No route matches the destination service name for this conversation. Create a route to the destination service name for messages in this conversation to be delivered.";
            return null;
        }
        if (!chosen.Address.Equals("LOCAL", StringComparison.OrdinalIgnoreCase))
        {
            status = from.Database.EncryptionKeys.MasterKey is null ? noKeys : "";
            return null;
        }
        if (from.Encrypted && from.Database.EncryptionKeys.MasterKey is null)
        {
            status = noKeys;
            return null;
        }
        status = "The target service name could not be found. Ensure that the service name is specified correctly and/or the routing information has been supplied.";
        if (from.Database.ServiceBroker.Services.TryGetValue(from.FarService, out var local))
            return local;
        BrokerService? elsewhere = null;
        foreach (var (_, database) in this.Databases)
        {
            if (ReferenceEquals(database, from.Database) || database.ServiceBrokerIfUsed is not { } objects || !objects.Services.TryGetValue(from.FarService, out var found))
                continue;
            if (elsewhere is not null)
                return null;
            elsewhere = found;
        }
        if (elsewhere is not null && !elsewhere.Queue.Schema.Database.BrokerEnabled)
        {
            status = "";
            return null;
        }
        return elsewhere;
    }

    /// <summary>
    /// The Error a target's queue answers a message with that fails its
    /// message type's validation, or null when it passes: an <c>EMPTY</c>
    /// type's body is Msg -8430 on a dialog's first message and -8499 on a
    /// later one, and an XML type's parse or schema failure -9615.
    /// </summary>
    private static (int Code, string Description)? ValidationFailure(BrokerMessageType type, byte[]? body, ConversationEndpoint from, long sequence, bool firstMessage)
    {
        var where = $" This occurred in the message with Conversation ID '{from.ConversationId.ToString().ToUpperInvariant()}', Initiator: {(from.IsInitiator ? 1 : 0)}, and Message sequence number: {sequence}.";
        switch (type.Validation)
        {
            case MessageValidation.Empty when body is not null:
                return firstMessage
                    ? (-8430, "The message body failed the configured validation.")
                    : (-8499, $"The remote service has sent a message body of type '{type.Name}' that does not match the message body encoding format." + where);
            case MessageValidation.WellFormedXml or MessageValidation.ValidXml when body is not null:
                try
                {
                    var xml = SqlValue.FromVarbinary(SqlType.VarbinaryMax, body).CoerceTo(SqlType.Xml);
                    if (type.Validation == MessageValidation.ValidXml)
                        _ = Cast.ValidateTypedXml(xml, type.Collection, document: false);
                    return null;
                }
                catch (SimulatedSqlException failed)
                {
                    return (-9615, $"A message of type '{type.Name}' failed XML validation on the target service.  {failed.Errors[0].Message}" + where);
                }
            default:
                return null;
        }
    }

    /// <summary>Creates the target's endpoint for the dialog <paramref name="from"/> opens, in <c>CONVERSING</c>, and moves the initiator there too.</summary>
    private static ConversationEndpoint CreateTargetEndpoint(BatchContext batch, BrokerRuntime broker, ConversationEndpoint from, BrokerService service, BrokerContract contract, DateTime now)
    {
        var owner = BrokerRuntime.PendingOwnerFor(batch);
        var database = service.Queue.Schema.Database;
        var group = new ConversationGroup(broker.NewId(), database, service) { PendingOwner = owner };
        broker.Groups[group.Key] = group;
        var target = new ConversationEndpoint(broker.NewId(), from.ConversationId, isInitiator: false, database, service, contract, group, from.Service.Name,
            Database.DboPrincipalId, from.Lifetime, from.Encrypted)
        {
            State = EndpointState.Conversing,
            FarPrincipalId = from.PrincipalId,
            FarBrokerInstance = BrokerInstanceOf(from.Database),
            Far = from,
            PendingOwner = owner,
        };
        broker.Endpoints[target.Key] = target;
        var (previousState, previousInstance) = (from.State, from.FarBrokerInstance);
        from.Far = target;
        from.State = EndpointState.Conversing;
        from.FarBrokerInstance = BrokerInstanceOf(database);
        broker.Log(batch, () =>
        {
            _ = broker.Endpoints.Remove(target.Key);
            _ = broker.Groups.Remove(group.Key);
            from.Far = null;
            from.State = previousState;
            from.FarBrokerInstance = previousInstance;
        }, () =>
        {
            target.PendingOwner = null;
            group.PendingOwner = null;
        });
        _ = now;
        return target;
    }

    /// <summary><paramref name="database"/>'s <c>service_broker_guid</c> as <c>far_broker_instance</c> spells it.</summary>
    internal static string BrokerInstanceOf(Database database) => database.ServiceBrokerGuid.ToString().ToUpperInvariant();

    /// <summary>
    /// Errors <paramref name="endpoint"/> once the sending transaction
    /// commits — real's broker reports a delivery failure after the fact —
    /// moving it to <c>ERROR</c> with an Error message on its queue; a
    /// rollback leaves it alone, the message never having gone.
    /// </summary>
    private static void ErrorEndpoint(BatchContext batch, BrokerRuntime broker, ConversationEndpoint endpoint, int code, string description, DateTime now)
    {
        var body = BrokerRuntime.ErrorBody(code, description);
        broker.Log(batch, static () => { }, () =>
        {
            if (endpoint.State == EndpointState.Closed || !broker.Endpoints.ContainsKey(endpoint.Key))
                return;
            endpoint.State = EndpointState.Error;
            _ = broker.Enqueue(null, endpoint, endpoint.Database.ServiceBroker.ErrorType, -1, body, now);
        });
    }

    private static void SetState(BatchContext batch, BrokerRuntime broker, ConversationEndpoint endpoint, EndpointState state)
    {
        var previous = endpoint.State;
        endpoint.State = state;
        broker.Log(batch, () => endpoint.State = previous, null);
    }

    /// <summary>Holds a message on its sender's <c>sys.transmission_queue</c>, every later message of the conversation joining it.</summary>
    private static void Hold(BatchContext batch, BrokerRuntime broker, ConversationEndpoint from, BrokerMessageType type, long sequence, byte[]? body, bool isError, bool isEnd, DateTime now, string status)
    {
        var message = new TransmissionMessage
        {
            From = from,
            Sequence = sequence,
            MessageTypeName = type.Name,
            IsError = isError,
            IsEndOfDialog = isEnd,
            Body = body,
            EnqueueTime = now,
            Status = status,
            PendingOwner = BrokerRuntime.PendingOwnerFor(batch),
        };
        broker.Transmission.Add(message);
        var previous = from.HeldStatus;
        if (status == HeldAfterError)
            from.HeldStatus = status;
        broker.Log(batch, () =>
        {
            _ = broker.Transmission.Remove(message);
            from.HeldStatus = previous;
        }, () => message.PendingOwner = null);
    }

    // ---- END CONVERSATION ----

    /// <summary>
    /// Parses and runs <c>END CONVERSATION @h [WITH ERROR = n DESCRIPTION =
    /// 'text' | WITH CLEANUP]</c>. The far side gets an EndDialog — or an Error
    /// carrying the code and text — and moves to <c>DISCONNECTED_INBOUND</c> or
    /// <c>ERROR</c>; an initiator's endpoint then goes, a target's stays
    /// <c>CLOSED</c>, and either's messages leave its queue. <c>CLEANUP</c>
    /// drops the endpoint telling no one (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    private static SimulatedNonQuery ParseEndConversation(ParserContext context)
    {
        context.MoveNextRequired(); // CONVERSATION
        context.MoveNextRequired();
        var handle = ParseBrokerOperand(context);
        Expression? code = null, description = null;
        var cleanup = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            if (AtWord(context, "CLEANUP"))
            {
                cleanup = true;
                context.MoveNextOptional();
            }
            else if (AtWord(context, "ERROR"))
            {
                RequireNextOperator(context, '=');
                context.MoveNextRequired();
                if (context.Token is Operator { Character: '-' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                code = Expression.Parse(context);
                if (!AtWord(context, "DESCRIPTION"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                RequireNextOperator(context, '=');
                context.MoveNextRequired();
                description = Expression.Parse(context);
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
        var batch = context.Batch;
        if (batch.IsSkipping)
            return NoBrokerCount;

        var runtime = new RuntimeContext(NoColumnResolver, batch);
        int? errorCode = null;
        string? errorText = null;
        if (code is not null)
        {
            var value = code.Run(runtime).CoerceTo(SqlType.BigInt);
            if (value.IsNull || value.AsInt64 is < 1 or > int.MaxValue)
                throw SimulatedSqlException.InvalidSubcommandValue(value.IsNull ? 0 : value.AsInt64, 2147483647, 3);
            errorCode = (int)value.AsInt64;
            errorText = description!.Run(runtime) is { IsNull: false } text ? text.CoerceTo(SqlType.NVarchar).AsString : "";
        }
        var id = EvaluateGuid(batch, handle) ?? throw SimulatedSqlException.ConversationHandleMissing();
        var database = batch.CurrentDatabase;
        var simulation = batch.Connection.Simulation;
        var broker = simulation.Broker;
        var now = batch.CurrentStatement.UtcNow;
        lock (broker.Gate)
        {
            broker.FireDue(now);
            var endpoint = broker.Find(database, id, batch.Connection.LockOwner, closed: cleanup) ?? throw SimulatedSqlException.ConversationHandleNotFound(id, 1);
            _ = broker.LockGroup(batch, endpoint.Group, skipWhenHeld: false);
            // A dialog that has sent nothing has no target to tell, so its end
            // only removes the initiator, an error included (probed 2026-10-20
            // against SQL Server 2025).
            if (!cleanup && endpoint.State == EndpointState.Conversing)
            {
                var sequence = endpoint.SendSequence;
                SetSendSequence(batch, broker, endpoint, sequence + 1);
                var objects = database.ServiceBroker;
                if (errorCode is { } errorNumber)
                    simulation.Deliver(batch, broker, endpoint, objects.ErrorType, -1, BrokerRuntime.ErrorBody(errorNumber, errorText!), isError: true, isEnd: false, now);
                else
                    simulation.Deliver(batch, broker, endpoint, objects.EndDialogType, sequence, null, isError: false, isEnd: true, now);
                if (errorCode is null)
                {
                    var previousEnd = endpoint.EndDialogSequence;
                    endpoint.EndDialogSequence = sequence;
                    broker.Log(batch, () => endpoint.EndDialogSequence = previousEnd, null);
                }
            }
            CloseEndpoint(batch, broker, endpoint, removes: endpoint.IsInitiator || cleanup);
        }
        return NoBrokerCount;
    }

    /// <summary>
    /// Ends <paramref name="endpoint"/>'s part in its dialog: its messages
    /// leave its queue and the transmission queue, and the endpoint goes —
    /// or, for a target, stays <c>CLOSED</c>, as real keeps one for replay
    /// protection.
    /// </summary>
    private static void CloseEndpoint(BatchContext batch, BrokerRuntime broker, ConversationEndpoint endpoint, bool removes)
    {
        var queue = endpoint.Service.Queue;
        var removedMessages = queue.Messages.FindAll(message => message.ConversationHandle == endpoint.Handle);
        _ = queue.Messages.RemoveAll(message => message.ConversationHandle == endpoint.Handle);
        var removedHeld = broker.Transmission.FindAll(message => ReferenceEquals(message.From, endpoint));
        _ = broker.Transmission.RemoveAll(message => ReferenceEquals(message.From, endpoint));
        var (previousState, previousTimer) = (endpoint.State, endpoint.DialogTimer);
        if (removes)
            _ = broker.Endpoints.Remove(endpoint.Key);
        else
            endpoint.State = EndpointState.Closed;
        endpoint.DialogTimer = null;
        broker.Log(batch, () =>
        {
            foreach (var message in removedMessages)
                InsertInOrder(queue.Messages, message);
            broker.Transmission.AddRange(removedHeld);
            broker.Endpoints[endpoint.Key] = endpoint;
            endpoint.State = previousState;
            endpoint.DialogTimer = previousTimer;
        }, null);
    }

    /// <summary>Puts <paramref name="message"/> back at its <c>queuing_order</c>.</summary>
    private static void InsertInOrder(List<QueuedMessage> messages, QueuedMessage message)
    {
        var index = messages.FindIndex(other => other.QueuingOrder > message.QueuingOrder);
        if (index < 0)
            messages.Add(message);
        else
            messages.Insert(index, message);
    }

    // ---- BEGIN CONVERSATION TIMER / MOVE CONVERSATION ----

    /// <summary>
    /// Parses and runs <c>BEGIN CONVERSATION TIMER (@h) TIMEOUT = n</c>: after
    /// <c>n</c> seconds (1 to 2147483646, Msg 8165 state 2) a DialogTimer
    /// message reaches the endpoint's own queue; a second timer replaces the
    /// first.
    /// </summary>
    private static SimulatedNonQuery ParseBeginConversationTimer(ParserContext context)
    {
        context.MoveNextRequired(); // CONVERSATION
        context.MoveNextRequired(); // TIMER
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var handle = ParseBrokerOperand(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        RequireNextWord(context, "TIMEOUT");
        RequireNextOperator(context, '=');
        context.MoveNextRequired();
        var timeout = Expression.Parse(context);
        var batch = context.Batch;
        if (batch.IsSkipping)
            return NoBrokerCount;

        var value = timeout.Run(new RuntimeContext(NoColumnResolver, batch)).CoerceTo(SqlType.BigInt);
        if (value.IsNull || value.AsInt64 is < 1 or > 2147483646)
            throw SimulatedSqlException.InvalidSubcommandValue(value.IsNull ? 0 : value.AsInt64, 2147483646, 2);
        var id = EvaluateGuid(batch, handle) ?? throw SimulatedSqlException.ConversationHandleMissing();
        var broker = batch.Connection.Simulation.Broker;
        var now = batch.CurrentStatement.UtcNow;
        lock (broker.Gate)
        {
            broker.FireDue(now);
            var endpoint = broker.Find(batch.CurrentDatabase, id, batch.Connection.LockOwner) ?? throw SimulatedSqlException.ConversationHandleNotFound(id, 3);
            var previous = endpoint.DialogTimer;
            endpoint.DialogTimer = now.AddSeconds(value.AsInt64);
            broker.Log(batch, () => endpoint.DialogTimer = previous, null);
        }
        return NoBrokerCount;
    }

    /// <summary>
    /// Parses and runs <c>MOVE CONVERSATION @h TO @g</c>: the endpoint and its
    /// queued messages join conversation group <c>@g</c>, which must be one of
    /// the same service (Msg 8439 otherwise).
    /// </summary>
    private static SimulatedNonQuery ParseMoveConversation(ParserContext context)
    {
        context.MoveNextRequired(); // CONVERSATION
        context.MoveNextRequired();
        var handle = ParseBrokerOperand(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.To })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var groupOperand = ParseBrokerOperand(context);
        var batch = context.Batch;
        if (batch.IsSkipping)
            return NoBrokerCount;

        var id = EvaluateGuid(batch, handle) ?? throw SimulatedSqlException.ConversationHandleMissing();
        var groupId = EvaluateGuid(batch, groupOperand) ?? Guid.Empty;
        var broker = batch.Connection.Simulation.Broker;
        var owner = batch.Connection.LockOwner;
        lock (broker.Gate)
        {
            var endpoint = broker.Find(batch.CurrentDatabase, id, owner) ?? throw SimulatedSqlException.ConversationHandleNotFound(id, 2);
            if (!broker.Groups.TryGetValue(new(batch.CurrentDatabase, groupId), out var group) || !BrokerRuntime.Sees(group.PendingOwner, owner) || !ReferenceEquals(group.Service, endpoint.Service))
                throw SimulatedSqlException.ConversationGroupInvalid(groupId);
            _ = broker.LockGroup(batch, endpoint.Group, skipWhenHeld: false);
            _ = broker.LockGroup(batch, group, skipWhenHeld: false);
            var previous = endpoint.Group;
            endpoint.Group = group;
            var moved = endpoint.Service.Queue.Messages.FindAll(message => message.ConversationHandle == endpoint.Handle);
            foreach (var message in moved)
                message.ConversationGroupId = group.Id;
            broker.Log(batch, () =>
            {
                endpoint.Group = previous;
                foreach (var message in moved)
                    message.ConversationGroupId = previous.Id;
            }, null);
        }
        return NoBrokerCount;
    }

    // ---- RECEIVE / GET CONVERSATION GROUP ----

    /// <summary>One item of a <c>RECEIVE</c> column list: an expression and the name it returns under, or the variable it assigns.</summary>
    private sealed class ReceiveItem(Expression expression, string name, string? variable)
    {
        public readonly Expression Expression = expression;
        public readonly string Name = name;
        public readonly string? Variable = variable;
    }

    /// <summary>
    /// Parses and runs <c>[WAITFOR (] RECEIVE [TOP (n)] columns FROM queue
    /// [INTO @t] [WHERE {conversation_handle | conversation_group_id} = x]
    /// [), TIMEOUT ms]</c>. It takes the messages of one conversation group
    /// no other transaction holds — the first with a message ready, or the
    /// one the <c>WHERE</c> names — in <c>queuing_order</c>, locks the group
    /// for its transaction, and returns, assigns or inserts its column list
    /// over them. A rollback puts them back, and the fifth in a row disables
    /// a queue handling poison messages. The <c>WAITFOR</c> form waits for a
    /// message to arrive, up to its timeout, then returns what it has — an
    /// empty set at the timeout.
    /// </summary>
    private static SimulatedStatementOutcome ParseReceive(ParserContext context, bool waits)
    {
        if (waits)
        {
            context.MoveNextRequired(); // (
            context.MoveNextRequired(); // RECEIVE
        }
        var batch = context.Batch;
        context.MoveNextRequired();
        Expression? top = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Top })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            top = Expression.Parse(context);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (top.IsWrittenConstant && top.Run(new RuntimeContext(NoColumnResolver, batch)) is { IsNull: false } constant && constant.CoerceTo(SqlType.BigInt).AsInt64 < 0)
                throw SimulatedSqlException.TopRowCountMustNotBeNegative();
            context.MoveNextRequired();
        }

        // The column list reads the queue's columns by name.
        var collation = batch.CurrentDatabase.Collation;
        var columns = BuiltInResources.QueueColumns;
        SqlType QueueColumnType(MultiPartName reference) =>
            reference.Count <= 2 && Array.Find(columns, column => collation.Equals(column.Name, reference.Leaf)) is { } column
                ? column.Type
                : throw SimulatedSqlException.InvalidColumnName(reference);
        var items = new List<ReceiveItem>();
        using (ParserScope.Enter(ref context.OuterTypeResolver, QueueColumnType))
        {
            while (true)
            {
                if (context.Token is Operator { Character: '*' })
                {
                    foreach (var column in columns)
                        items.Add(new ReceiveItem(new Reference(new MultiPartName(column.Name)), column.Name, null));
                    context.MoveNextRequired();
                }
                else if (context.Token is AtPrefixedString variable && PeekAfterLead(context) is Operator { Character: '=' })
                {
                    context.MoveNextRequired();
                    context.MoveNextRequired();
                    var assigned = Expression.Parse(context);
                    items.Add(new ReceiveItem(assigned, variable.Value, variable.Value));
                }
                else
                {
                    if (context.Token is Name && PeekAfterLead(context) is Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context.GetNextRequired());
                    var expression = Expression.Parse(context);
                    var name = expression.Name;
                    if (context.Token is ReservedKeyword { Keyword: Keyword.As })
                    {
                        name = context.GetNextRequired<Name>().Value;
                        context.MoveNextRequired();
                    }
                    else if (context.Token is Name alias && !AtWord(context, "FROM"))
                    {
                        name = alias.Value;
                        context.MoveNextRequired();
                    }
                    items.Add(new ReceiveItem(expression, name, null));
                }
                if (context.Token is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
        }
        var assigns = items.Exists(static item => item.Variable is not null);
        if (assigns && items.Exists(static item => item.Variable is null))
            throw SimulatedSqlException.ReceiveAssignmentMixedWithRetrieval();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.From })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var queueName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();
        OutputTarget? into = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Into })
            into = TryParseOutputIntoTarget(context, items.Count, queueName.Leaf, logged: null);
        Expression? filter = null;
        var byGroup = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            if (context.Token is Name { Value: var column } && BuiltInToken.EqualsAny(column, "conversation_handle", "conversation_group_id"))
                byGroup = BuiltInToken.Equals(column, "conversation_group_id");
            else
                throw SimulatedSqlException.SyntaxErrorNear(context);
            RequireNextOperator(context, '=');
            context.MoveNextRequired();
            // The operand is a variable or a character literal, compared with
            // the column — anything else, NULL included, is a syntax error
            // (probed 2026-10-20 against SQL Server 2025).
            if (context.Token is not (AtPrefixedString or Literal { Value.Type: VarcharSqlType or NVarcharSqlType }))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            filter = Expression.Parse(context);
            var filterType = filter.GetSqlType(batch, static name => throw SimulatedSqlException.InvalidColumnName(name));
            // Real's compile reports a handle's clash twice, a group's once.
            if (SqlType.OperandPairError(TypePairOperation.Compare, new TypePairOperand(SqlType.UniqueIdentifier), new TypePairOperand(filterType, filter), "equal to") is { } clash)
                throw byGroup ? clash : SimulatedSqlException.Aggregate([clash, clash.CopyOfError()]);
        }
        var timeout = 0L;
        if (waits)
        {
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            timeout = ParseWaitForTimeout(context);
        }

        var schema = new SqlType[items.Count];
        for (var i = 0; i < items.Count; i++)
            schema[i] = items[i].Expression.GetSqlType(batch, QueueColumnType);
        into?.RequireAssignable(items.ConvertAll(static item => item.Expression), schema);
        if (batch.IsSkipping)
            return NoBrokerCount;

        if (!batch.TryResolveQueue(queueName, out var queue))
            throw SimulatedSqlException.InvalidObjectName(queueName, 12);
        RequireQueueReceive(batch, queue, 42);
        var runtime = new RuntimeContext(NoColumnResolver, batch);
        long? limit = null;
        if (top is not null)
        {
            var value = top.Run(runtime);
            if (value.IsNull)
                throw SimulatedSqlException.TopClauseInvalidValue();
            limit = value.CoerceTo(SqlType.BigInt).AsInt64;
            if (limit < 0)
                throw SimulatedSqlException.TopRowCountMustNotBeNegative();
        }
        // A failure from here on sends the statement's empty result set
        // ahead of a CATCH block's output.
        if (!assigns && into is null)
            batch.CurrentStatement.ClientOutputShape = (schema, [.. items.Select(static item => item.Name)]);
        Guid? filterId = null;
        if (filter is not null)
        {
            // A handle must name a conversation of the database, if not one
            // of this queue's, where a group that names none receives nothing.
            filterId = EvaluateGuid(batch, filter)
                ?? throw (byGroup ? SimulatedSqlException.ConversationGroupMissing(20) : SimulatedSqlException.ConversationHandleMissing(20));
            if (!byGroup)
            {
                var broker = batch.Connection.Simulation.Broker;
                lock (broker.Gate)
                {
                    if (broker.Find(batch.CurrentDatabase, filterId.Value, batch.Connection.LockOwner) is null)
                        throw SimulatedSqlException.ConversationHandleNotFound(filterId.Value, 20);
                }
            }
        }

        var received = TakeMessages(batch, queue, limit, filterId, byGroup, timeout, lockOnly: false, out _);
        var rows = new List<SqlValue[]>(received.Count);
        foreach (var message in received)
        {
            var row = BuiltInResources.QueueRow(message);
            SqlValue Column(MultiPartName name)
            {
                for (var i = 0; i < columns.Length; i++)
                {
                    if (collation.Equals(columns[i].Name, name.Leaf))
                        return row[i];
                }
                throw SimulatedSqlException.InvalidColumnName(name);
            }
            var itemRuntime = new RuntimeContext(Column, batch);
            var values = new SqlValue[items.Count];
            for (var i = 0; i < items.Count; i++)
                values[i] = items[i].Expression.Run(itemRuntime);
            if (assigns)
            {
                for (var i = 0; i < items.Count; i++)
                    AssignBrokerVariable(batch, items[i].Variable!, values[i]);
            }
            else if (into is not null)
            {
                into.Append(values, batch);
            }
            else
            {
                rows.Add(values);
            }
        }
        if (assigns || into is not null)
            return new SimulatedNonQuery(received.Count, countsRowsReturned: into is null);
        return new SimulatedSqlResultSet(schema, [.. items.Select(static item => item.Name)], rows)
        {
            RecordsAffected = rows.Count,
            ColumnNullability = [.. items.Select(item => item.Expression is not Reference reference
                || Array.Find(columns, column => collation.Equals(column.Name, reference.Name)) is not { } column || column.Nullable)],
        };
    }

    /// <summary>Reads <c>, TIMEOUT n</c> after a <c>WAITFOR (…)</c>, -1 (wait for ever) when absent.</summary>
    private static long ParseWaitForTimeout(ParserContext context)
    {
        if (context.Token is not Operator { Character: ',' })
            return -1;
        RequireNextWord(context, "TIMEOUT");
        context.MoveNextRequired();
        var timeout = Expression.Parse(context);
        if (context.Batch.IsSkipping)
            return -1;
        var value = timeout.Run(new RuntimeContext(NoColumnResolver, context.Batch)).CoerceTo(SqlType.BigInt);
        return value.IsNull ? -1 : Math.Max(value.AsInt64, -1);
    }

    /// <summary>
    /// Takes the next conversation group's messages from <paramref name="queue"/>
    /// for <paramref name="batch"/>'s transaction, waiting up to
    /// <paramref name="timeout"/> milliseconds (-1 for ever, 0 not at all) for
    /// one to arrive; with <paramref name="lockOnly"/> it only locks the group
    /// (<c>GET CONVERSATION GROUP</c>), whose id <paramref name="group"/>
    /// returns.
    /// </summary>
    private static List<QueuedMessage> TakeMessages(BatchContext batch, ServiceQueue queue, long? limit, Guid? filterId, bool byGroup, long timeout, bool lockOnly, out Guid? group)
    {
        var broker = batch.Connection.Simulation.Broker;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var session = batch.Connection.Session;
        var waited = false;
        try
        {
            lock (broker.Gate)
            {
                while (true)
                {
                    broker.FireDue(DateTime.UtcNow);
                    // The fifth RECEIVE in a row a rollback undid disabled a
                    // queue handling poison messages, which the next RECEIVE
                    // meets (probed 2026-10-16 against SQL Server 2025).
                    if (!queue.Settings.Enabled)
                        throw SimulatedSqlException.QueueDisabled(queue.Name);
                    if (FindReceivable(broker, batch, queue, filterId, byGroup) is { } found)
                    {
                        group = found.Id;
                        _ = broker.LockGroup(batch, found, skipWhenHeld: false);
                        return lockOnly ? [] : Receive(batch, broker, queue, found, filterId, byGroup, limit);
                    }
                    var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (timeout == 0 || (timeout > 0 && elapsed >= timeout))
                    {
                        group = null;
                        return [];
                    }
                    if (!waited)
                    {
                        waited = true;
                        session.WaitStartedTicks = Environment.TickCount64;
                        session.WaitForType = "BROKER_RECEIVE_WAITFOR";
                        session.InWaitFor = true;
                    }
                    var slice = timeout < 0 ? 50 : (int)Math.Clamp(timeout - elapsed, 1, 50);
                    _ = Monitor.Wait(broker.Gate, slice);
                    batch.ThrowIfCancelled();
                }
            }
        }
        finally
        {
            if (waited)
            {
                session.InWaitFor = false;
                session.WaitForType = "WAITFOR";
                var span = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                batch.Connection.WaitedTicks += span.Ticks;
                batch.Connection.Simulation.RecordWait(session, "BROKER_RECEIVE_WAITFOR", (long)span.TotalMilliseconds);
            }
        }
    }

    /// <summary>
    /// The conversation group a <c>RECEIVE</c> takes from: the one the filter
    /// names, else the group of the first ready message no other transaction
    /// holds; null when none has a message to take.
    /// </summary>
    private static ConversationGroup? FindReceivable(BrokerRuntime broker, BatchContext batch, ServiceQueue queue, Guid? filterId, bool byGroup)
    {
        var owner = batch.Connection.LockOwner;
        foreach (var message in queue.Messages)
        {
            if (message.Status != 1 || message.ReceivedBy is not null || !BrokerRuntime.Sees(message.PendingOwner, owner))
                continue;
            if (filterId is { } id && (byGroup ? message.ConversationGroupId : message.ConversationHandle) != id)
                continue;
            if (!broker.Groups.TryGetValue(new(queue.Schema.Database, message.ConversationGroupId), out var group))
                continue;
            if (group.LockedBy is { } holder && !ReferenceEquals(holder, owner))
                continue;
            return group;
        }
        return null;
    }

    /// <summary>Receives the ready messages of <paramref name="group"/> (or of the one conversation the filter names), up to <paramref name="limit"/>.</summary>
    private static List<QueuedMessage> Receive(BatchContext batch, BrokerRuntime broker, ServiceQueue queue, ConversationGroup group, Guid? filterId, bool byGroup, long? limit)
    {
        var owner = batch.Connection.LockOwner;
        var taken = new List<QueuedMessage>();
        foreach (var message in queue.Messages)
        {
            if (limit is { } most && taken.Count >= most)
                break;
            if (message.ConversationGroupId != group.Id || message.Status != 1 || message.ReceivedBy is not null || !BrokerRuntime.Sees(message.PendingOwner, owner))
                continue;
            if (filterId is { } id && !byGroup && message.ConversationHandle != id)
                continue;
            taken.Add(message);
        }
        if (taken.Count == 0)
            return taken;
        var retention = queue.Settings.Retention;
        foreach (var message in taken)
            message.ReceivedBy = owner;
        broker.Log(batch, () =>
        {
            foreach (var message in taken)
                message.ReceivedBy = null;
            // A rolled-back RECEIVE counts toward the queue's poison message
            // handling, the fifth in a row disabling the queue as it rolls
            // back (probed 2026-10-16 against SQL Server 2025).
            if (++queue.ConsecutiveRollbacks >= 5 && queue.Settings.PoisonMessageHandling && queue.Settings.Enabled)
            {
                queue.Settings.Enabled = false;
                batch.Connection.Simulation.NoteQueueDisabledEvent(queue, queue.Schema.Database, batch.Connection);
            }
        }, () =>
        {
            queue.ConsecutiveRollbacks = 0;
            queue.ReceivedCount++;
            batch.Connection.Simulation.NoteQueueReceived(queue);
            foreach (var message in taken)
            {
                message.ReceivedBy = null;
                if (retention)
                    message.Status = 0;
                else
                    _ = queue.Messages.Remove(message);
            }
        });
        return taken;
    }

    /// <summary>
    /// Parses and runs <c>[WAITFOR (] GET CONVERSATION GROUP @g FROM queue
    /// [), TIMEOUT ms]</c>: <c>@g</c> takes the id of the group the next
    /// <c>RECEIVE</c> would read, now locked for the transaction, or NULL when
    /// no message is ready.
    /// </summary>
    private static SimulatedNonQuery ParseGetConversationGroup(ParserContext context, bool waits)
    {
        if (waits)
        {
            context.MoveNextRequired(); // (
            context.MoveNextRequired(); // GET
        }
        context.MoveNextRequired(); // CONVERSATION
        if (!IsKeyWord(context.GetNextRequired(), "GROUP"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var variable = ParseBrokerOutputVariable(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.From })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var queueName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();
        var timeout = 0L;
        if (waits)
        {
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            timeout = ParseWaitForTimeout(context);
        }
        var batch = context.Batch;
        if (batch.IsSkipping)
            return NoBrokerCount;
        if (!batch.TryResolveQueue(queueName, out var queue))
            throw SimulatedSqlException.InvalidObjectName(queueName, 11);
        RequireQueueReceive(batch, queue, 42);
        _ = TakeMessages(batch, queue, null, null, byGroup: false, timeout, lockOnly: true, out var group);
        AssignBrokerVariable(batch, variable, group is { } id ? SqlValue.FromGuid(id) : SqlValue.Null(SqlType.UniqueIdentifier));
        return NoBrokerCount;
    }
}
