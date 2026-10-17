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

    /// <summary>The coroutine whose statement the calling thread runs, while it runs one.</summary>
    [ThreadStatic]
    public static StatementCoroutine? Running;

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
        EngineThreads.Run(() => this.RunStatement(statement));
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
        Running = this;
        OnAnyStatementThread = true;
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
            Running = null;
            OnAnyStatementThread = false;
            Volatile.Write(ref this.statementThreadId, 0);
            this.ended = true;
            _ = this.toCaller.Release();
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
