using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

/// <summary>
/// The Service Broker switches <c>ALTER DATABASE … SET</c> and <c>RESTORE …
/// WITH</c> take: whether a database's broker delivers, and what becomes of
/// its broker id and its conversations.
/// </summary>
internal enum BrokerSwitch : byte
{
    Enable,
    Disable,

    /// <summary><c>NEW_BROKER</c>: a new broker id, every conversation gone without a word, the broker enabled.</summary>
    New,

    /// <summary><c>ERROR_BROKER_CONVERSATIONS</c>: every conversation errored, the broker enabled.</summary>
    ErrorConversations,
}

// A database's broker instance — its service_broker_guid, whether its broker
// is enabled, and the switches that change both — probed 2026-10-16 against
// SQL Server 2025; see docs/claude/service-broker.md.
partial class Simulation
{
    /// <summary>
    /// Applies <paramref name="change"/> to <paramref name="database"/>'s
    /// broker. False, having changed nothing, when it would enable a broker
    /// whose id another database's enabled broker carries — what a restored
    /// copy beside its original meets.
    /// </summary>
    internal bool TrySwitchBroker(BatchContext batch, Database database, BrokerSwitch change)
    {
        switch (change)
        {
            case BrokerSwitch.Disable:
                database.BrokerEnabled = false;
                return true;
            case BrokerSwitch.New:
                this.EndConversationsOf(database, errored: false);
                database.ServiceBrokerGuid = Guid.NewGuid();
                break;
            default:
                if (this.BrokerIdEnabledElsewhere(database))
                    return false;
                if (change == BrokerSwitch.ErrorConversations)
                    this.EndConversationsOf(database, errored: true);
                break;
        }
        database.BrokerEnabled = true;
        this.RedeliverHeld(batch, database, BrokerDisabledStatus);
        this.CatalogRows.Invalidate();
        return true;
    }

    /// <summary>Whether a database other than <paramref name="database"/> has an enabled broker under its broker id.</summary>
    private bool BrokerIdEnabledElsewhere(Database database)
    {
        foreach (var (_, other) in this.Databases)
        {
            if (!ReferenceEquals(other, database) && other.BrokerEnabled && other.ServiceBrokerGuid == database.ServiceBrokerGuid && database.ServiceBrokerGuid != Guid.Empty)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Ends every conversation of <paramref name="database"/>, outside any
    /// transaction. <paramref name="errored"/>: <c>ERROR_BROKER_CONVERSATIONS</c>,
    /// each endpoint left <c>ERROR</c> with Msg -9719's Error on its own queue;
    /// otherwise <c>NEW_BROKER</c>, every endpoint, group and message of the
    /// database gone without a word.
    /// </summary>
    private void EndConversationsOf(Database database, bool errored)
    {
        if (this.BrokerIfUsed is not { } broker)
            return;
        lock (broker.Gate)
        {
            if (errored)
            {
                var now = DateTime.UtcNow;
                foreach (var (_, endpoint) in broker.Endpoints.OrderBy(static pair => pair.Value.Handle))
                {
                    if (!ReferenceEquals(endpoint.Database, database) || endpoint.PendingOwner is not null || endpoint.State is EndpointState.Closed or EndpointState.Error)
                        continue;
                    endpoint.State = EndpointState.Error;
                    endpoint.DialogTimer = null;
                    _ = broker.Enqueue(null, endpoint, database.ServiceBroker.ErrorType, -1,
                        BrokerRuntime.ErrorBody(-9719, "The database for this conversation endpoint is attached or restored."), now);
                }
            }
            else
            {
                broker.Forget(database);
                if (database.ServiceBrokerIfUsed is { } objects)
                {
                    foreach (var queue in objects.AllQueues(database))
                        queue.Messages.Clear();
                }
            }
            Monitor.PulseAll(broker.Gate);
        }
    }
}
