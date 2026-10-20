namespace SqlServerSimulator;

// Service Broker error factories: the DDL of message types, contracts,
// queues, services, routes, remote service bindings and conversation
// priorities, and the conversation statements (probed 2026-10-16 against
// SQL Server 2025).
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 15151: a Service Broker object a statement names that doesn't
    /// exist — <paramref name="verb"/> is <c>find</c>, <c>alter</c> or
    /// <c>drop</c> and <paramref name="kind"/> the class (<c>message type</c>,
    /// <c>contract</c>, <c>queue</c>, <c>service</c>, <c>route</c>, …).
    /// </summary>
    internal static SimulatedSqlException BrokerObjectNotFound(string verb, string kind, string name, byte state = 1) =>
        new($"Cannot {verb} the {kind} '{name}', because it does not exist or you do not have permission.", 15151, 16, state);

    /// <summary>Msg 262 for a Service Broker <c>CREATE</c> without its permission: state 18 for a queue, 17 for the others.</summary>
    internal static SimulatedSqlException BrokerCreatePermissionDenied(string permission, string databaseName, byte state) =>
        new($"{permission} permission denied in database '{databaseName}'.", 262, 14, state) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 9663: an <c>ALTER</c> or <c>DROP</c> of an object every database
    /// ships — <c>ALTER MESSAGE TYPE</c> state 1, <c>ALTER QUEUE</c> 3,
    /// <c>ALTER SERVICE</c> 6, <c>DROP SERVICE</c> 7, <c>DROP CONTRACT</c> 8,
    /// <c>DROP MESSAGE TYPE</c> 9.
    /// </summary>
    internal static SimulatedSqlException SystemBrokerObjectCannotBeModified(byte state) =>
        new("The system object cannot be modified.", 9663, 16, state);

    /// <summary>
    /// Msg 8426: no endpoint answers a conversation handle — state 1 for
    /// <c>END CONVERSATION</c>, 2 <c>MOVE CONVERSATION</c>, 3
    /// <c>BEGIN CONVERSATION TIMER</c>, 4 <c>SEND</c>, 6 a
    /// <c>RELATED_CONVERSATION</c>.
    /// </summary>
    internal static SimulatedSqlException ConversationHandleNotFound(Guid handle, byte state) =>
        new($"The conversation handle \"{handle.ToString().ToUpperInvariant()}\" is not found.", 8426, 16, state);

    /// <summary>Msg 8418: a NULL conversation handle — state 5, 6 for a <c>RELATED_CONVERSATION</c>, 20 for <c>RECEIVE … WHERE</c>'s.</summary>
    internal static SimulatedSqlException ConversationHandleMissing(byte state = 5) =>
        new("The conversation handle is missing. Specify a conversation handle.", 8418, 16, state);

    /// <summary>Msg 8420: a NULL conversation group where a statement needs one, such as <c>RECEIVE … WHERE conversation_group_id</c>'s.</summary>
    internal static SimulatedSqlException ConversationGroupMissing(byte state) =>
        new("The conversation group is missing. Specify a conversation group.", 8420, 16, state);

    /// <summary>Msg 8429: <c>SEND</c> on an endpoint that has ended or errored, naming its state code.</summary>
    internal static SimulatedSqlException EndpointStateRefusesSend(string state) =>
        new($"The conversation endpoint is not in a valid state for SEND. The current endpoint state is '{state}'.", 8429, 16, 1);

    /// <summary>Msg 8431: <c>SEND</c> of a message type the conversation's contract doesn't name.</summary>
    internal static SimulatedSqlException MessageTypeNotInContract(string type) =>
        new($"The message type '{type}' is not part of the service contract.", 8431, 16, 1);

    /// <summary>Msg 8432: <c>SEND</c> of a message type the contract lets only the other side send.</summary>
    internal static SimulatedSqlException MessageTypeWrongSide(string type, bool initiator) =>
        new(initiator
            ? $"The message cannot be sent because the message type '{type}' is marked SENT BY TARGET in the contract, however this service is an Initiator."
            : $"The message cannot be sent because the message type '{type}' is marked SENT BY INITIATOR in the contract, however this service is a Target.", 8432, 16, 1);

    /// <summary>Msg 8433: <c>SEND</c> of a NULL body.</summary>
    internal static SimulatedSqlException MessageBodyNull() =>
        new("The message body may not be NULL.  A zero-length UNICODE or binary string is allowed.", 8433, 16, 1);

    /// <summary>Msg 8411: a dialog <c>LIFETIME</c> outside 1 to 2147483647.</summary>
    internal static SimulatedSqlException DialogLifetimeOutOfRange(long value) =>
        new($"The dialog lifetime value of {value} is outside the allowable range of 1 to 2147483647. Specify a valid dialog lifetime value.", 8411, 16, 1);

    /// <summary>
    /// Msg 8165: an <c>END CONVERSATION … WITH ERROR</c> code (state 3, up to
    /// 2147483647) or a conversation timer's timeout (state 2, up to
    /// 2147483646) below 1.
    /// </summary>
    internal static SimulatedSqlException InvalidSubcommandValue(long value, long maximum, byte state) =>
        new($"Invalid subcommand value {value}. Legal range from 1 to {maximum}.", 8165, 16, state);

    /// <summary>Msg 8439: <c>MOVE CONVERSATION</c> to a group that doesn't exist.</summary>
    internal static SimulatedSqlException ConversationGroupInvalid(Guid group) =>
        new($"The destination conversation group '{group.ToString().ToUpperInvariant()}' is invalid.", 8439, 16, 1);

    /// <summary>Msg 8447: a <c>RECEIVE</c> column list mixing variable assignments with returned columns.</summary>
    internal static SimulatedSqlException ReceiveAssignmentMixedWithRetrieval() =>
        new("A RECEIVE statement that assigns a value to a variable must not be combined with data retrieval operations.", 8447, 15, 1);

    /// <summary>Msg 9617: <c>RECEIVE</c> from a queue whose status is off.</summary>
    internal static SimulatedSqlException QueueDisabled(string queue) =>
        new($"The service queue \"{queue}\" is currently disabled.", 9617, 16, 1);

    /// <summary>
    /// Msg 1088: a queue its reader lacks <c>RECEIVE</c> on — state 41 for
    /// <c>BEGIN DIALOG</c>'s initiator queue, 42 for <c>RECEIVE</c>.
    /// </summary>
    internal static SimulatedSqlException QueueNotFoundOrDenied(string queue, byte state) =>
        new($"Cannot find the object \"{queue}\" because it does not exist or you do not have permissions.", 1088, 16, state);

    /// <summary>Msg 8421: <c>BEGIN DIALOG</c> to a NULL service name.</summary>
    internal static SimulatedSqlException BrokerTargetServiceMissing() =>
        new("The service name is missing.  Specify a service name.", 8421, 16, 2);

    /// <summary>Msg 8403: a contract naming one message type twice.</summary>
    internal static SimulatedSqlException MessageTypeSpecifiedTwice(string name) =>
        new($"The message type '{name}' is specified more than once.  Remove the duplicate message type.", 8403, 16, 1);

    /// <summary>Msg 8404: a service naming one contract twice, or <c>ALTER SERVICE … ADD</c> of a contract it carries.</summary>
    internal static SimulatedSqlException ServiceContractSpecifiedTwice(string name) =>
        new($"The service contract '{name}' is specified more than once.  Remove the duplicate service contract.", 8404, 16, 1);

    /// <summary>
    /// Msg 3716: a message type a contract names, a contract or queue a
    /// service names — <paramref name="kind"/> bound to one or more
    /// <paramref name="boundTo"/> — can't be dropped; state 10 for a message
    /// type, 11 for a contract, 14 for a queue.
    /// </summary>
    internal static SimulatedSqlException BrokerObjectBound(string kind, string name, string boundTo, byte state) =>
        new($"The {kind} '{name}' cannot be dropped because it is bound to one or more {boundTo}.", 3716, 16, state);

    /// <summary>Msg 187: <c>MAX_QUEUE_READERS</c> outside 0 to 32767.</summary>
    internal static SimulatedSqlException MaxQueueReadersOutOfRange() =>
        new("The valid range for MAX_QUEUE_READERS is 0 to 32767.", 187, 15, 1);

    /// <summary>Msg 416: an <c>INSERT</c>, <c>UPDATE</c> or <c>DELETE</c> naming a queue.</summary>
    internal static SimulatedSqlException QueueCannotBeUpdated(string name) =>
        new($"The service queue \"{name}\" cannot be directly updated.", 416, 16, 1);

    /// <summary>Msg 1921 state 11: <c>CREATE QUEUE … ON</c> a filegroup the database lacks.</summary>
    internal static SimulatedSqlException QueueFilegroupInvalid(string name) =>
        new($"Invalid filegroup '{name}' specified.", 1921, 16, 11);

    /// <summary>Msg 2812 state 64: an activation naming a procedure that doesn't exist.</summary>
    internal static SimulatedSqlException ActivationProcedureNotFound(string name) =>
        new($"Could not find stored procedure '{name}'.", 2812, 16, 64);

    /// <summary>Msg 9744: a route whose <c>ADDRESS</c> is no address.</summary>
    internal static SimulatedSqlException RouteAddressInvalid(string route) =>
        new($"The ADDRESS of route \"{route}\" is not a valid address.", 9744, 16, 1);

    /// <summary>Msg 33302: a <c>PRIORITY_LEVEL</c> outside 1 to 10.</summary>
    internal static SimulatedSqlException PriorityLevelInvalid(string name) =>
        new($"The PRIORITY_LEVEL that is specified for conversation priority '{name}' is not valid. The value must be between 1 and 10.", 33302, 16, 1);

    /// <summary>Msg 33303: a conversation priority whose name or properties another one has.</summary>
    internal static SimulatedSqlException PriorityAlreadyExists(string name, string? contract, string? localService, string? remoteService) =>
        new($"A conversation priority already exists in the database with either the name '{name}' or the properties CONTRACT_NAME='{contract ?? "NULL"}', LOCAL_SERVICE_NAME='{localService ?? "NULL"}', and REMOTE_SERVICE_NAME='{remoteService ?? "NULL"}'. Either use a unique name or a unique set of properties.", 33303, 16, 1);
    /// <summary>
    /// Mimics SQL Server error 9772, then Msg 5069: <c>ALTER DATABASE … SET
    /// ENABLE_BROKER</c> or <c>ERROR_BROKER_CONVERSATIONS</c> of a database
    /// whose broker id another database's enabled broker carries — a restored
    /// copy beside its original (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException BrokerIdAlreadyEnabled(string databaseName) =>
        FollowedByAlterDatabaseFailed(new($"The Service Broker in database \"{databaseName}\" cannot be enabled because there is already an enabled Service Broker with the same ID.", 9772, 16, 1));

    /// <summary>
    /// Msg 9773 state 2, informational: a restore asked to enable a broker
    /// whose id another database's enabled broker carries leaves it disabled,
    /// naming the database by id (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static SimulatedError RestoredBrokerDisabledMessage(Parser.BatchContext batch, int databaseId) =>
        batch.InfoMessage(@class: 0, state: 2, number: 9773, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"The Service Broker in database \"{databaseId}\" is disabled because there is already an enabled Service Broker with the same ID."));
    /// <summary>
    /// Mimics SQL Server error 1095: an event notification's list naming an
    /// event twice, directly or through a group, which names the first event
    /// the later item repeats (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException EventTypeAlreadySpecified(string eventType) =>
        new($"\"{eventType}\" has already been specified as an event type.", 1095, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 2104 state 5: <c>CREATE EVENT NOTIFICATION</c>
    /// without its permission, which ends the batch and rolls back as under
    /// <c>XACT_ABORT</c>, dooming the transaction when caught (probed
    /// 2026-10-20 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException EventNotificationPermissionDenied(string name) =>
        new($"Cannot create the event notification '{name}', because you do not have permission.", 2104, 14, 5) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Mimics SQL Server error 2116: <c>'current database'</c> naming a
    /// database with no broker id — <c>master</c> or <c>model</c> — which ends
    /// the batch as under <c>XACT_ABORT</c> (probed 2026-10-20 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException EventNotificationTargetNotBrokerDatabase(string databaseName) =>
        new($"Cannot CREATE EVENT NOTIFICATION to database '{databaseName}' because it is not a valid broker database.", 2116, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Mimics SQL Server error 2102: a server's event notification, which
    /// sends from msdb (state 2), created by a login with no user there, which
    /// ends the batch as under <c>XACT_ABORT</c> (probed 2026-10-20 against
    /// SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException EventNotificationNoUser(string name, string loginName, string databaseName, byte state) =>
        new($"Cannot create event notification '{name}' since there is no user for login '{loginName}' in database '{databaseName}'.", 2102, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Mimics SQL Server error 15151 for <c>DROP EVENT NOTIFICATION</c>, which
    /// ends the batch and drops none of the list (probed 2026-10-16 against
    /// SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException EventNotificationNotFound(string name) =>
        new($"Cannot drop the event notification '{name}', because it does not exist or you do not have permission.", 15151, 16, 1) { TerminatesBatch = true };
    /// <summary>Mimics SQL Server error 15349: <c>ALTER SCHEMA … TRANSFER</c> of an object real ships — one of every database's queues.</summary>
    internal static SimulatedSqlException CannotTransferMsShippedObject() =>
        new("Cannot transfer an MS Shipped object.", 15349, 16, 1);
}
