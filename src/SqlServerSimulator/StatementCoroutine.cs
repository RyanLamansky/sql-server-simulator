using System.Runtime.ExceptionServices;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// A statement run on a thread of its own, so what it sends from deep inside
/// its own execution — a trigger body's result sets, messages and counts —
/// goes out as it is produced rather than once the statement ends, as real
/// sends a trigger's result set while its firing statement is still running
/// (probed 2026-10-09 against SQL Server 2025: a reader two rows into a
/// trigger's 2,000-row result held the firing update's X and the trigger
/// read's position, the trigger's statements after its <c>SELECT</c> not yet
/// run, the request <c>suspended</c> on <c>ASYNC_NETWORK_IO</c>).
/// </summary>
/// <remarks>
/// <para>
/// The caller's thread and the statement's hand control back and forth, one
/// running at a time: the caller's outcome stream (<see cref="Run"/>) waits
/// while the statement runs, and the statement waits in <see cref="Send"/>
/// while the caller hands what it sent on and its consumer reads — so nothing
/// the two share is ever touched by both at once. While the statement waits it
/// reports no executing thread, as a statement suspended on its client does,
/// and the request it belongs to holds the session
/// (<see cref="SessionRequest.MidStatement"/>), which real can't interleave
/// with another.
/// </para>
/// <para>
/// A consumer that goes away disposes the outcome stream, which ends the
/// statement where it waits: <see cref="Send"/> throws, the statement unwinds
/// through its own error handling — its writes rolled back — and the caller
/// waits for it. A consumer collected without disposing it is noticed the same
/// way, so the thread doesn't keep the session alive.
/// </para>
/// </remarks>
internal sealed class StatementCoroutine(SimulatedDbConnection connection) : IDisposable
{
    private readonly SemaphoreSlim toStatement = new(0, 1);

    private readonly SemaphoreSlim toCaller = new(0, 1);

    private SimulatedStatementOutcome? sent;

    private volatile bool ended;

    private volatile bool abandoned;

    private ExceptionDispatchInfo? fault;

    private int statementThreadId;

    /// <summary>The caller's outcome stream as it runs, while it is reachable.</summary>
    private WeakReference<object>? consumer;

    /// <summary>How often a statement waiting on its caller checks that the caller is still reachable.</summary>
    private const int ConsumerCheckMilliseconds = 500;

    /// <summary>Whether the calling thread is the statement's.</summary>
    public bool OnStatementThread => Environment.CurrentManagedThreadId == Volatile.Read(ref this.statementThreadId);

    /// <summary>
    /// Whether the calling thread runs a statement on its own: a result its
    /// statements stream then names its consumer weakly
    /// (<see cref="ResultStream.HoldsConsumerWeakly"/>), since the thread
    /// keeps the session — and what names the consumer through it — reachable.
    /// </summary>
    [ThreadStatic]
    public static bool OnAnyStatementThread;

    /// <summary>
    /// Runs <paramref name="statement"/> on a thread of its own, answering each
    /// outcome it sends as it sends it, and ends once it returns, with what
    /// escaped it thrown here.
    /// </summary>
    public IEnumerable<SimulatedStatementOutcome> Run(Action statement)
    {
        var running = new object();
        this.consumer = new WeakReference<object>(running);
        var request = connection.ExecutingRequest;
        MarkMidStatement(request, true);
        Worker.Run(() => this.RunStatement(statement));
        try
        {
            while (true)
            {
                this.toCaller.Wait();
                if (this.ended)
                    break;
                var outcome = this.sent!;
                this.sent = null;
                yield return outcome;
                _ = this.toStatement.Release();
            }
        }
        finally
        {
            if (!this.ended)
            {
                this.abandoned = true;
                _ = this.toStatement.Release();
                while (!this.ended)
                    this.toCaller.Wait();
            }
            MarkMidStatement(request, false);
            this.Dispose();
            GC.KeepAlive(running);
        }
        this.fault?.Throw();
    }

    private static void MarkMidStatement(SessionRequest? request, bool mid)
    {
        if (request is null)
            return;
        request.MidStatement = mid;
    }

    private void RunStatement(Action statement)
    {
        Volatile.Write(ref this.statementThreadId, Environment.CurrentManagedThreadId);
        try
        {
            using var culture = CultureScope.Engine();
            connection.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
            DateOrder.Current = connection.DateFormat;
            Language.Current = connection.Language;
            statement();
        }
        catch (StatementAbandonedException) when (this.abandoned)
        {
        }
#pragma warning disable CA1031 // Whatever escapes the statement is the caller's to meet, rethrown on its own thread.
        catch (Exception escaped)
#pragma warning restore CA1031
        {
            this.fault = ExceptionDispatchInfo.Capture(escaped);
        }
        finally
        {
            Volatile.Write(ref this.statementThreadId, 0);
            this.ended = true;
            _ = this.toCaller.Release();
        }
    }

    /// <summary>
    /// A thread statements run on, kept idle between them for a while so a
    /// statement doesn't pay for a thread of its own each time it runs.
    /// </summary>
    private sealed class Worker : IDisposable
    {
        /// <summary>How long an idle worker waits for its next statement before its thread ends.</summary>
        private const int IdleMilliseconds = 10_000;

        private static readonly Lock IdleGate = new();

        /// <summary>The idle workers, the latest first, linked through <see cref="nextIdle"/>.</summary>
        private static Worker? idle;

        private readonly SemaphoreSlim woken = new(0, 1);

        private Worker? nextIdle;

        private Action? job;

        /// <summary>Runs <paramref name="statement"/> on an idle worker's thread, or a new one's.</summary>
        public static void Run(Action statement)
        {
            Worker? worker;
            lock (IdleGate)
            {
                worker = idle;
                if (worker is not null)
                {
                    idle = worker.nextIdle;
                    worker.nextIdle = null;
                }
            }
            if (worker is not null)
            {
                worker.job = statement;
                _ = worker.woken.Release();
                return;
            }
#pragma warning disable CA2000 // The worker's own thread disposes it as it retires.
            worker = new Worker { job = statement };
#pragma warning restore CA2000
            new Thread(worker.Loop) { IsBackground = true, Name = "Simulated statement" }.Start();
        }

        private void Loop()
        {
            OnAnyStatementThread = true;
            while (true)
            {
                var statement = this.job!;
                this.job = null;
                statement();
                lock (IdleGate)
                {
                    this.nextIdle = idle;
                    idle = this;
                }
                while (!this.woken.Wait(IdleMilliseconds))
                {
                    // Retire unless a caller took this worker meanwhile, whose
                    // wake is on its way.
                    lock (IdleGate)
                    {
                        if (Unlink(this))
                        {
                            this.Dispose();
                            return;
                        }
                    }
                }
            }
        }

        public void Dispose() => this.woken.Dispose();

        /// <summary>Takes <paramref name="worker"/> out of the idle list, if it is still in it.</summary>
        private static bool Unlink(Worker worker)
        {
            if (ReferenceEquals(idle, worker))
            {
                idle = worker.nextIdle;
                worker.nextIdle = null;
                return true;
            }
            for (var at = idle; at is not null; at = at.nextIdle)
            {
                if (ReferenceEquals(at.nextIdle, worker))
                {
                    at.nextIdle = worker.nextIdle;
                    worker.nextIdle = null;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Lets go of the two handoff signals, once both threads are done with them.</summary>
    public void Dispose()
    {
        this.toStatement.Dispose();
        this.toCaller.Dispose();
    }

    /// <summary>
    /// Hands <paramref name="outcome"/> to the caller and waits until its
    /// consumer reads on; throws <see cref="StatementAbandonedException"/> once
    /// the consumer has gone, which the statement unwinds through.
    /// </summary>
    public void Send(SimulatedStatementOutcome outcome)
    {
        if (this.abandoned)
            throw new StatementAbandonedException();
        this.sent = outcome;
        var executing = connection.CurrentExecutingThreadId;
        connection.CurrentExecutingThreadId = null;
        _ = this.toCaller.Release();
        while (!this.toStatement.Wait(ConsumerCheckMilliseconds))
        {
            if (!this.consumer!.TryGetTarget(out _))
            {
                this.abandoned = true;
                break;
            }
        }
        connection.CurrentExecutingThreadId = executing;
        // Another request may have run meanwhile, publishing its own.
        DateOrder.Current = connection.DateFormat;
        Language.Current = connection.Language;
        if (this.abandoned)
            throw new StatementAbandonedException();
    }
}

/// <summary>
/// Ends a statement running on a thread of its own whose consumer went away
/// (<see cref="StatementCoroutine"/>), unwinding it through its own cleanup;
/// never a client's to see.
/// </summary>
#pragma warning disable CA1064 // Never leaves the statement's own thread, where the coroutine catches it.
internal sealed class StatementAbandonedException : Exception
#pragma warning restore CA1064
{
    public StatementAbandonedException()
        : base("The statement's consumer went away.")
    {
    }

    public StatementAbandonedException(string message)
        : base(message)
    {
    }

    public StatementAbandonedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
