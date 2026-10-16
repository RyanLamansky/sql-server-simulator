using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>A conversation endpoint's <c>sys.conversation_endpoints.state</c>.</summary>
internal enum EndpointState : byte
{
    StartedOutbound,
    Conversing,
    DisconnectedInbound,
    Error,
    Closed,
}

/// <summary>
/// One side of a dialog — <c>sys.conversation_endpoints</c>. The initiator's
/// is created by <c>BEGIN DIALOG</c>, the target's by the first message that
/// reaches its service; <see cref="Far"/> links them once both exist. Read
/// and written under <see cref="BrokerRuntime.Gate"/>.
/// </summary>
internal sealed class ConversationEndpoint(Guid handle, Guid conversationId, bool isInitiator, Database database, BrokerService service, BrokerContract contract, ConversationGroup group, string farService, int principalId, DateTime lifetime, bool encrypted)
{
    public readonly Guid Handle = handle;
    public readonly Guid ConversationId = conversationId;
    public readonly bool IsInitiator = isInitiator;
    public readonly Database Database = database;
    public readonly BrokerService Service = service;
    public readonly BrokerContract Contract = contract;
    public ConversationGroup Group = group;
    public readonly string FarService = farService;
    public string? FarBrokerInstance;
    public readonly int PrincipalId = principalId;
    public int FarPrincipalId = -1;
    public DateTime Lifetime = lifetime;
    public readonly bool Encrypted = encrypted;
    public EndpointState State = EndpointState.StartedOutbound;

    /// <summary>When <c>BEGIN CONVERSATION TIMER</c>'s timer fires; null while none is set.</summary>
    public DateTime? DialogTimer;

    /// <summary>The next <c>message_sequence_number</c> a message this endpoint sends takes.</summary>
    public long SendSequence;

    /// <summary>How many of the far side's messages reached this endpoint.</summary>
    public long ReceiveSequence;

    /// <summary>The sequence number of the EndDialog this endpoint sent, -1 while it has sent none.</summary>
    public long EndDialogSequence = -1;

    public ConversationEndpoint? Far;

    /// <summary>
    /// Why this endpoint's messages wait on <c>sys.transmission_queue</c>
    /// rather than reaching the far service, once one did; every later
    /// message joins them.
    /// </summary>
    public string? HeldStatus;

    /// <summary>The owner of the open transaction that created this endpoint; null once committed.</summary>
    public object? PendingOwner;

    public string StateCode => this.State switch
    {
        EndpointState.Closed => "CD",
        EndpointState.Conversing => "CO",
        EndpointState.DisconnectedInbound => "DI",
        EndpointState.Error => "ER",
        _ => "SO",
    };

    public string StateDescription => this.State switch
    {
        EndpointState.Closed => "CLOSED",
        EndpointState.Conversing => "CONVERSING",
        EndpointState.DisconnectedInbound => "DISCONNECTED_INBOUND",
        EndpointState.Error => "ERROR",
        _ => "STARTED_OUTBOUND",
    };
}

/// <summary>
/// A conversation group — <c>sys.conversation_groups</c>: what a
/// <c>RECEIVE</c> takes its messages from and locks for its transaction.
/// </summary>
internal sealed class ConversationGroup(Guid id, Database database, BrokerService service)
{
    public readonly Guid Id = id;
    public readonly Database Database = database;
    public readonly BrokerService Service = service;

    /// <summary>The transaction owner holding the group, until its transaction ends.</summary>
    public object? LockedBy;

    /// <summary>The owner of the open transaction that created the group; null once committed.</summary>
    public object? PendingOwner;
}

/// <summary>One message waiting on its sender's <c>sys.transmission_queue</c>.</summary>
internal sealed class TransmissionMessage
{
    public ConversationEndpoint From = null!;
    public long Sequence;
    public string MessageTypeName = "";
    public bool IsError, IsEndOfDialog;
    public byte[]? Body;
    public DateTime EnqueueTime;
    public string Status = "";
    public object? PendingOwner;
}

/// <summary>
/// The conversation state of one <see cref="Simulation"/>'s Service Broker:
/// every endpoint, conversation group and held message across its
/// databases, the delivery of messages to the queues their services read,
/// and the timers and lifetimes that fire as time passes. Created on the
/// first conversation statement (<see cref="Simulation.Broker"/>). Every read
/// and write takes <see cref="Gate"/>, which a <c>WAITFOR (RECEIVE …)</c>
/// waits on; a change a transaction makes is logged on its undo log
/// (<see cref="UndoLog.RecordBrokerChange"/>) so a rollback reverses it and
/// a commit publishes it.
/// </summary>
internal sealed class BrokerRuntime(Simulation simulation)
{
    public readonly object Gate = new();
    public readonly Dictionary<Guid, ConversationEndpoint> Endpoints = [];
    public readonly Dictionary<Guid, ConversationGroup> Groups = [];
    public readonly List<TransmissionMessage> Transmission = [];

    /// <summary>
    /// Logs <paramref name="undo"/> and <paramref name="commit"/> on the
    /// statement's undo log, each run under <see cref="Gate"/> and waking any
    /// waiting <c>RECEIVE</c>; outside one the change is already committed.
    /// </summary>
    public void Log(BatchContext batch, Action undo, Action? commit)
    {
        if (LogOf(batch) is { } log)
        {
            log.RecordBrokerChange(
                () =>
                {
                    lock (this.Gate)
                    {
                        undo();
                        Monitor.PulseAll(this.Gate);
                    }
                },
                commit is null ? this.Wake : () =>
                {
                    lock (this.Gate)
                    {
                        commit();
                        Monitor.PulseAll(this.Gate);
                    }
                });
        }
        else
        {
            commit?.Invoke();
            Monitor.PulseAll(this.Gate);
        }
    }

    /// <summary>
    /// The undo log a change <paramref name="batch"/> makes rides: its
    /// statement's, or under a DDL statement its transaction's; null when the
    /// change commits as it is made.
    /// </summary>
    private static UndoLog? LogOf(BatchContext batch) => batch.CurrentUndoLog ?? batch.Connection.CurrentTransaction?.UndoLog;

    /// <summary>The owner a change <paramref name="batch"/> makes is pending on, null when it commits as it is made.</summary>
    public static object? PendingOwnerFor(BatchContext batch) => LogOf(batch) is null ? null : batch.Connection.LockOwner;

    private void Wake()
    {
        lock (this.Gate)
            Monitor.PulseAll(this.Gate);
    }

    /// <summary>Whether <paramref name="owner"/> sees something another open transaction made.</summary>
    public static bool Sees(object? pendingOwner, object owner) => pendingOwner is null || ReferenceEquals(pendingOwner, owner);

    public Guid NewId() => simulation.GenerateNewSequentialId();

    /// <summary>
    /// The endpoint <paramref name="handle"/> names in <paramref name="database"/>
    /// as <paramref name="owner"/> sees it, or null — a closed one too, which
    /// no statement reaches.
    /// </summary>
    public ConversationEndpoint? Find(Database database, Guid handle, object owner) =>
        this.Endpoints.TryGetValue(handle, out var endpoint) && ReferenceEquals(endpoint.Database, database)
            && endpoint.State != EndpointState.Closed && Sees(endpoint.PendingOwner, owner)
            ? endpoint
            : null;

    /// <summary>
    /// Takes <paramref name="group"/> for the transaction <paramref name="batch"/>
    /// runs in, waiting while another holds it; false when
    /// <paramref name="skipWhenHeld"/> and another holds it.
    /// </summary>
    public bool LockGroup(BatchContext batch, ConversationGroup group, bool skipWhenHeld)
    {
        var owner = batch.Connection.LockOwner;
        while (group.LockedBy is { } holder && !ReferenceEquals(holder, owner))
        {
            if (skipWhenHeld)
                return false;
            _ = Monitor.Wait(this.Gate, 25);
            batch.ThrowIfCancelled();
        }
        if (group.LockedBy is not null || LogOf(batch) is null)
            return true;
        group.LockedBy = owner;
        this.Log(batch, () => group.LockedBy = null, () => group.LockedBy = null);
        return true;
    }

    /// <summary>
    /// Fires the dialog timers and lifetimes due by <paramref name="now"/>: a
    /// timer puts a DialogTimer message on its own endpoint's queue, and an
    /// expired lifetime errors both sides with Msg -8489's Error message
    /// (probed 2026-10-16 against SQL Server 2025). Neither belongs to a
    /// transaction.
    /// </summary>
    public void FireDue(DateTime now)
    {
        List<ConversationEndpoint>? expired = null;
        foreach (var (_, endpoint) in this.Endpoints)
        {
            if (endpoint.PendingOwner is not null || endpoint.State == EndpointState.Closed)
                continue;
            if (endpoint.DialogTimer is { } timer && timer <= now)
            {
                endpoint.DialogTimer = null;
                var broker = endpoint.Database.ServiceBroker;
                _ = this.Enqueue(null, endpoint, broker.DialogTimerType, -2, null, now);
            }
            if (endpoint.Lifetime <= now && endpoint.State is EndpointState.StartedOutbound or EndpointState.Conversing)
                (expired ??= []).Add(endpoint);
        }
        if (expired is null)
            return;
        foreach (var endpoint in expired)
        {
            if (endpoint.State == EndpointState.Error)
                continue;
            foreach (var side in endpoint.Far is { } far ? [endpoint, far] : (ConversationEndpoint[])[endpoint])
            {
                side.State = EndpointState.Error;
                _ = this.Enqueue(null, side, side.Database.ServiceBroker.ErrorType, -1, ErrorBody(-8489, "The dialog has exceeded the specified LIFETIME."), now);
            }
        }
        Monitor.PulseAll(this.Gate);
    }

    /// <summary>
    /// Puts a message for <paramref name="endpoint"/> on its service's queue —
    /// one its far side sent, or one Service Broker sends it — pending on
    /// <paramref name="batch"/>'s transaction when one is given.
    /// </summary>
    public QueuedMessage Enqueue(BatchContext? batch, ConversationEndpoint endpoint, BrokerMessageType type, long sequence, byte[]? body, DateTime now)
    {
        var queue = endpoint.Service.Queue;
        var message = new QueuedMessage
        {
            QueuingOrder = queue.NextQueuingOrder++,
            ConversationGroupId = endpoint.Group.Id,
            ConversationHandle = endpoint.Handle,
            SequenceNumber = sequence,
            ServiceName = endpoint.Service.Name,
            ServiceId = endpoint.Service.Id,
            ContractName = endpoint.Contract.Name,
            ContractId = endpoint.Contract.Id,
            MessageTypeName = type.Name,
            MessageTypeId = type.Id,
            Validation = type.ValidationCode,
            Body = body,
            EnqueueTime = now,
            PendingOwner = batch is null ? null : PendingOwnerFor(batch),
        };
        queue.Messages.Add(message);
        if (batch is not null)
            this.Log(batch, () => _ = queue.Messages.Remove(message), () => message.PendingOwner = null);
        return message;
    }

    /// <summary>
    /// The UTF-16 body of an Error message: real's XML document with its byte
    /// order mark, the description's markup characters escaped (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static byte[] ErrorBody(int code, string description)
    {
        var text = new StringBuilder()
            .Append("\uFEFF<?xml version=\"1.0\"?><Error xmlns=\"http://schemas.microsoft.com/SQL/ServiceBroker/Error\"><Code>")
            .Append(code.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("</Code><Description>");
        foreach (var c in description)
        {
            _ = c switch
            {
                '&' => text.Append("&amp;"),
                '<' => text.Append("&lt;"),
                '>' => text.Append("&gt;"),
                '\'' => text.Append("&apos;"),
                '"' => text.Append("&quot;"),
                _ => text.Append(c),
            };
        }
        _ = text.Append("</Description></Error>");
        return Encoding.Unicode.GetBytes(text.ToString());
    }
}
