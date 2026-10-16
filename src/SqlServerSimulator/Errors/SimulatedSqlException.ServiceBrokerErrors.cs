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
}
