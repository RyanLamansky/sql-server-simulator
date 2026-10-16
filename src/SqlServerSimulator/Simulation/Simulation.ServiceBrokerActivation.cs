using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

// Service Broker activation: a queue's activation procedure, started on a
// background session of its own when a message reaches the queue (probed
// 2026-10-16 against SQL Server 2025); see docs/claude/service-broker.md.
partial class Simulation
{
    /// <summary>One activation procedure running on its background session — a row of <c>sys.dm_broker_activated_tasks</c>.</summary>
    internal sealed class ActivatedTask(ServiceQueue queue, Database database, string procedure, int executeAs)
    {
        public readonly ServiceQueue Queue = queue;
        public readonly Database Database = database;
        public readonly string Procedure = procedure;
        public readonly int ExecuteAs = executeAs;
        public readonly DateTime Started = DateTime.UtcNow;
        public int Spid;
    }

    /// <summary>The activation procedures running, guarded by itself.</summary>
    internal readonly List<ActivatedTask> ActivatedTasks = [];

    /// <summary>
    /// The queues whose last activated reader ended having received nothing,
    /// which a later message no longer activates until something receives
    /// from them — <c>sys.dm_broker_queue_monitors</c>' <c>NOTIFIED</c>.
    /// </summary>
    internal readonly HashSet<ServiceQueue> NotifiedQueues = [];

    /// <summary>
    /// Starts <paramref name="queue"/>'s activation procedure on a background
    /// session when a message has reached it, its activation is on, and
    /// fewer readers than <c>MAX_QUEUE_READERS</c> run. Called under
    /// <see cref="BrokerRuntime.Gate"/> as the message commits; the procedure
    /// runs on a thread of its own.
    /// </summary>
    internal void NoteQueueArrival(ServiceQueue queue, Database database)
    {
        this.NoteQueueActivationEvent(queue, database);
        var settings = queue.Settings;
        if (!settings.ActivationEnabled || !settings.Enabled || settings.ActivationName is null || settings.MaxReaders <= 0 || !database.BrokerEnabled)
            return;
        ActivatedTask task;
        lock (this.ActivatedTasks)
        {
            if (this.NotifiedQueues.Contains(queue))
                return;
            var running = 0;
            foreach (var other in this.ActivatedTasks)
            {
                if (ReferenceEquals(other.Queue, queue))
                    running++;
            }
            // One reader starts at once; another only once the ones running
            // have had five seconds to keep up, as real's queue monitor
            // ramps up (probed 2026-10-16 against SQL Server 2025).
            if (running >= settings.MaxReaders || (running > 0 && !this.ActivatedTasks.TrueForAll(other => !ReferenceEquals(other.Queue, queue) || DateTime.UtcNow - other.Started >= TimeSpan.FromSeconds(5))))
                return;
            var executeAs = settings.ExecuteAsPrincipalId is null or -2 ? Database.DboPrincipalId : settings.ExecuteAsPrincipalId.Value;
            task = new ActivatedTask(queue, database, settings.ActivationProcedure!, executeAs);
            this.ActivatedTasks.Add(task);
        }
        _ = Task.Run(() => this.RunActivation(task, settings.ActivationSchema!, settings.ActivationName));
    }

    /// <summary>
    /// Starts <paramref name="queue"/>'s reader when its activation has just
    /// been turned on and messages already wait on it.
    /// </summary>
    internal void KickActivation(ServiceQueue queue, Database database)
    {
        lock (this.ActivatedTasks)
            _ = this.NotifiedQueues.Remove(queue);
        if (this.QueueHasReadyMessages(queue))
            this.NoteQueueArrival(queue, database);
    }

    /// <summary>
    /// The state and waiting-task count <c>sys.dm_broker_queue_monitors</c>
    /// reports for <paramref name="queue"/>.
    /// </summary>
    internal (string State, int Waiting) QueueMonitorState(ServiceQueue queue)
    {
        lock (this.ActivatedTasks)
        {
            var running = this.ActivatedTasks.Count(task => ReferenceEquals(task.Queue, queue));
            return running > 0 ? ("RECEIVES_OCCURRING", running)
                : this.NotifiedQueues.Contains(queue) ? ("NOTIFIED", 0)
                : ("INACTIVE", 0);
        }
    }

    /// <summary>
    /// A <c>RECEIVE</c> took a message from <paramref name="queue"/>, so a
    /// message reaching it activates its procedure again.
    /// </summary>
    internal void NoteQueueReceived(ServiceQueue queue)
    {
        lock (this.ActivatedTasks)
            _ = this.NotifiedQueues.Remove(queue);
        this.NoteQueueEventReceived(queue);
    }

    /// <summary>
    /// Runs one activated reader: <c>EXECUTE</c>s the procedure in the queue's
    /// database as the activation's principal, again for as long as it
    /// receives something and messages remain, writing each error and
    /// message it raises to the error log as real does — <c>The activated
    /// proc '…' running on queue '…' output the following:  '…'</c>.
    /// </summary>
    private void RunActivation(ActivatedTask task, string schema, string procedure)
    {
        var queue = task.Queue;
        var database = task.Database;
        var queueName = $"{database.Name}.{queue.Schema.Name}.{queue.Name}";
        try
        {
            using var connection = this.CreateDbConnection();
            connection.InfoMessage += (_, e) => this.LogActivationOutput(task, queueName, e.Message);
            connection.Open();
            task.Spid = connection.Spid;
            var user = task.ExecuteAs == Database.DboPrincipalId ? null
                : database.Principals.EnumerateValues().FirstOrDefault(principal => principal.PrincipalId == task.ExecuteAs)?.Name;
            var text = $"use {QuoteIdentifier(database.Name)};{(user is null ? "" : $" execute as user = N'{user.Replace("'", "''", StringComparison.Ordinal)}';")} exec {QuoteIdentifier(schema)}.{QuoteIdentifier(procedure)};";
            while (true)
            {
                var receivedBefore = queue.ReceivedCount;
                using (var command = connection.CreateCommand())
                {
#pragma warning disable CA2100 // The text names a catalog procedure and principal, each quoted.
                    command.CommandText = text;
#pragma warning restore CA2100
                    command.CommandTimeout = 0;
                    try
                    {
                        _ = command.ExecuteNonQuery();
                    }
                    catch (SimulatedSqlException failed)
                    {
                        foreach (var error in failed.Errors)
                            this.LogActivationOutput(task, queueName, error.Message);
                    }
                }
                var received = queue.ReceivedCount != receivedBefore;
                if (!received)
                {
                    lock (this.ActivatedTasks)
                        _ = this.NotifiedQueues.Add(queue);
                    break;
                }
                if (!queue.Settings.ActivationEnabled || !this.QueueHasReadyMessages(queue))
                    break;
            }
        }
        catch (Exception unexpected) when (unexpected is not OutOfMemoryException)
        {
            this.LogActivationOutput(task, queueName, unexpected.Message);
        }
        finally
        {
            lock (this.ActivatedTasks)
                _ = this.ActivatedTasks.Remove(task);
        }
    }

    private bool QueueHasReadyMessages(ServiceQueue queue)
    {
        if (this.BrokerIfUsed is not { } broker)
            return false;
        lock (broker.Gate)
            return queue.Messages.Exists(static message => message.Status == 1 && message.PendingOwner is null && message.ReceivedBy is null);
    }

    private void LogActivationOutput(ActivatedTask task, string queueName, string text) =>
        this.ErrorLog.Write(DateTime.UtcNow, $"spid{task.Spid}s", $"The activated proc '{task.Procedure}' running on queue '{queueName}' output the following:  '{text}'");
}
