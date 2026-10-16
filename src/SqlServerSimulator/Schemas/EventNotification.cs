namespace SqlServerSimulator.Schemas;

/// <summary>
/// What an event notification is created on — <c>sys.event_notifications</c>'
/// and <c>sys.server_event_notifications</c>' <c>parent_class</c>.
/// </summary>
internal enum EventNotificationParent : byte
{
    Database = 0,
    Queue = 1,
    Server = 100,
}

/// <summary>
/// One event notification (<c>CREATE EVENT NOTIFICATION</c>): the events it
/// takes and the service its messages go to. A database's and its queues'
/// live on <see cref="Database.EventNotifications"/>, the server's on
/// <see cref="Simulation.ServerEventNotifications"/>. Each sends on one
/// dialog of its own from the <c>EventNotificationService</c> of its home
/// database — its own database, or msdb for a server's — which
/// <see cref="Dialog"/> holds once created.
/// </summary>
internal sealed class EventNotification(string name, int objectId, EventNotificationParent parent, Database home, ServiceQueue? queue, TriggerEventType[] events,
    int?[] groups, DateTime createDate, string serviceName, string brokerInstance, byte[]? creatorSid, int principalId)
{
    public readonly string Name = name;
    public readonly int ObjectId = objectId;
    public readonly EventNotificationParent Parent = parent;

    /// <summary>The database whose <c>EventNotificationService</c> the dialog begins from: the notification's own, msdb for a server's.</summary>
    public readonly Database Home = home;

    /// <summary>The queue an <see cref="EventNotificationParent.Queue"/> notification watches.</summary>
    public readonly ServiceQueue? Queue = queue;

    /// <summary>The leaf events the notification takes, in the order <c>sys.events</c> lists them.</summary>
    public readonly TriggerEventType[] Events = events;

    /// <summary>For each of <see cref="Events"/>, the group it was named through, or null.</summary>
    public readonly int?[] Groups = groups;

    public readonly DateTime CreateDate = createDate;
    public readonly string ServiceName = serviceName;

    /// <summary>The broker instance the target service is reached in, as <c>broker_instance</c> reports it — <c>'current database'</c> already read as the database's id.</summary>
    public readonly string BrokerInstance = brokerInstance;

    /// <summary><c>creator_sid</c>: the creating login's sid under <c>WITH FAN_IN</c>, else null.</summary>
    public readonly byte[]? CreatorSid = creatorSid;

    /// <summary>The owner: a database principal, or for a server notification the login's server principal.</summary>
    public readonly int PrincipalId = principalId;

    /// <summary>The initiator's endpoint of the notification's dialog; a new one begins when a broker switch or restore ended it.</summary>
    public ConversationEndpoint? Dialog;

    /// <summary>Whether the notification takes the leaf event <paramref name="type"/>.</summary>
    public bool Takes(int type)
    {
        foreach (var entry in this.Events)
        {
            if (entry.Type == type)
                return true;
        }
        return false;
    }
}
