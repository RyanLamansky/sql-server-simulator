using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SqlServerSimulator;

/// <summary>
/// The threads the engine runs work on that no caller brought: a TDS session's
/// requests (<see cref="Context"/>), a statement run beside its caller
/// (<see cref="StatementCoroutine"/>) and an activated queue reader. None comes
/// from the shared .NET thread pool, because the engine blocks the thread it
/// runs on for as long as a statement waits — a <c>WAITFOR</c>, a lock wait, a
/// settle — and the pool adds threads past its floor only gradually, so a host
/// with many waiting sessions would starve every other pool user, the
/// endpoint's own logins included.
/// </summary>
/// <remarks>
/// A job runs on an idle worker if there is one and otherwise on a new thread
/// started at once, so blocked work never waits for a thread to free up; a
/// worker left idle for <see cref="IdleMilliseconds"/> retires. Work that waits
/// on a socket instead waits on no thread at all: an <c>async</c> method
/// running on <see cref="Context"/> resumes on a worker once what it awaited
/// completes, wherever that completes.
/// </remarks>
internal static class EngineThreads
{
    /// <summary>How long an idle worker waits for its next job before its thread ends.</summary>
    private const int IdleMilliseconds = 10_000;

    /// <summary>
    /// How many short spins a worker that just went idle makes before it
    /// sleeps: a client reading its response sends its next request within
    /// microseconds, and a worker still spinning takes it without the wake a
    /// sleeping one costs.
    /// </summary>
    private const int IdleSpins = 50;

    private static readonly Lock IdleGate = new();

    /// <summary>The idle workers, the latest first, linked through <see cref="Worker.NextIdle"/>.</summary>
    private static Worker? idle;

    /// <summary>
    /// The synchronization context every worker runs under, which posts each
    /// continuation awaited on it to a worker: an <c>await</c> that keeps its
    /// context (<c>ConfigureAwait(true)</c>) inside work started by
    /// <see cref="RunAsync"/> resumes on a worker, and one that drops it
    /// resumes on whatever thread completed the awaited operation — usually the
    /// shared pool's.
    /// </summary>
    public static readonly EngineSynchronizationContext Context = new();

    /// <summary>Runs <paramref name="job"/> on an idle worker's thread, or a new one's.</summary>
    public static void Run(Action job)
    {
        Worker? worker;
        lock (IdleGate)
        {
            worker = idle;
            if (worker is not null)
            {
                idle = worker.NextIdle;
                worker.NextIdle = null;
            }
        }
        if (worker is not null)
        {
            worker.Job = job;
            _ = worker.Woken.Release();
            return;
        }
        // The worker's own thread disposes it as it retires. The thread starts
        // with no execution context of its own: each job brings its own.
        worker = new Worker { Job = job };
        new Thread(worker.Loop) { IsBackground = true, Name = "Simulated engine" }.UnsafeStart();
    }

    /// <summary>
    /// Starts <paramref name="start"/> on a worker, under the caller's
    /// execution context, and completes as the task it returns does; every
    /// continuation inside it that keeps its context resumes on a worker.
    /// </summary>
    public static Task RunAsync(Func<Task> start)
    {
        var context = ExecutionContext.Capture();
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            try
            {
                if (context is null)
                {
                    started.SetResult(start());
                }
                else
                {
                    ExecutionContext.Run(context, static state =>
                    {
                        var (start, started) = ((Func<Task>, TaskCompletionSource<Task>))state!;
                        started.SetResult(start());
                    }, (start, started));
                }
            }
#pragma warning disable CA1031 // Whatever escapes the start is the caller's to meet, through the task.
            catch (Exception escaped)
#pragma warning restore CA1031
            {
                _ = started.TrySetException(escaped);
            }
        });
        return started.Task.Unwrap();
    }

    /// <summary>
    /// Fails, in a Debug build, when the calling thread is the shared pool's:
    /// the engine work of a TDS session runs on a worker, or on a thread a
    /// statement started of its own (a background production), and an
    /// <c>await</c> dropping its context (<c>ConfigureAwait(false)</c>) on the
    /// way would send it to the pool instead.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertOffSharedPool()
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
            throw new InvalidOperationException("Engine work of a TDS session ran on the shared thread pool.");
    }

    /// <summary>One worker thread and the job handed to it.</summary>
    private sealed class Worker : IDisposable
    {
        public readonly SemaphoreSlim Woken = new(0, 1);

        public Worker? NextIdle;

        public Action? Job;

        public void Loop()
        {
            SynchronizationContext.SetSynchronizationContext(Context);
            while (true)
            {
                this.RunJob();
                lock (IdleGate)
                {
                    this.NextIdle = idle;
                    idle = this;
                }
                for (var spin = 0; spin < IdleSpins && this.Woken.CurrentCount == 0; spin++)
                    Thread.SpinWait(20);
                while (!this.Woken.Wait(IdleMilliseconds))
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

        /// <summary>
        /// Runs the job handed over, in a frame of its own, so the idle thread
        /// that ran it names nothing of it — a statement's batch and session
        /// would otherwise stay reachable until the thread runs another.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RunJob()
        {
            var job = this.Job!;
            this.Job = null;
            job();
        }

        public void Dispose() => this.Woken.Dispose();
    }

    /// <summary>Takes <paramref name="worker"/> out of the idle list, if it is still in it.</summary>
    private static bool Unlink(Worker worker)
    {
        if (ReferenceEquals(idle, worker))
        {
            idle = worker.NextIdle;
            worker.NextIdle = null;
            return true;
        }
        for (var at = idle; at is not null; at = at.NextIdle)
        {
            if (ReferenceEquals(at.NextIdle, worker))
            {
                at.NextIdle = worker.NextIdle;
                worker.NextIdle = null;
                return true;
            }
        }
        return false;
    }

    /// <summary>Posts each callback to a worker (<see cref="Context"/>).</summary>
    internal sealed class EngineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => Run(() => d(state));

        public override SynchronizationContext CreateCopy() => this;
    }
}
