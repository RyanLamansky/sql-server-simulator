using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// A <c>SELECT</c> whose rows are produced as its client reads them, as real
/// sends a result set: the statement runs ahead of the client by what the
/// client's transport holds, then waits — <c>suspended</c> on
/// <c>ASYNC_NETWORK_IO</c> — still holding the locks, the snapshot and the
/// schema stability its position needs, and carries on as the client reads.
/// The rows sit in a buffer from their production until the client reads
/// them, so a large result costs a window of rows rather than all of them.
/// </summary>
/// <remarks>
/// <para>
/// The statement is suspended inside its batch's outcome stream: the dispatch
/// loop yields the result set, then a <see cref="Marker"/> each time it has
/// produced another window, and runs on only when the consumer — the
/// in-process data reader, the TDS endpoint's row writer — advances the
/// stream again through <see cref="Pull"/>. Every row is so produced inside
/// the outcome stream's own advance, which is where a request's state, its
/// cancellation, its transaction membership and the engine's culture are
/// already in place. Once the source ends, the statement ends — its count,
/// its error, its locks — and a last marker hands the advance back at the
/// statement's end, before anything the next statement does.
/// </para>
/// <para>
/// Only a top-level command a streaming consumer runs produces one
/// (<see cref="SimulatedDbCommand.StreamsResultRows"/>), and only for a
/// <c>SELECT</c> whose outcomes reach that consumer directly; a result whose
/// rows all fit the first window completes before it is sent, exactly as a
/// materialized one.
/// </para>
/// </remarks>
internal abstract class ResultStream
{
    /// <summary>
    /// Real's TDS packet, the unit the client receives a response in; the
    /// window counts the packet the client is reading plus the four
    /// <see cref="SessionRequest.BytesAheadOfClient"/> grants ahead of it.
    /// Probed 2026-10-08 against SQL Server 2025 over a MARS connection, a
    /// <c>REPEATABLE READ</c> reader two rows into a result held the key locks
    /// of 20 rows of 2,007 wire bytes, 79 of 507 and 4,438 of 9 — about
    /// 40,000 bytes produced in each case.
    /// </summary>
    private protected const int PacketBytes = 8000;

    /// <summary>The rows produced so far, the statement's count once it ends.</summary>
    public int RowCount;

    /// <summary>The error a row raised, which ended the statement after the rows before it; null otherwise.</summary>
    public SimulatedSqlException? Error;

    /// <summary>Whether the statement has produced its last row, or abandoned the rest.</summary>
    public bool Complete;

    /// <summary>
    /// Set once the consumer reads no more of the rows — a reader moving to
    /// the next result or closing: the rest are produced as real produces
    /// what its client discards, counted, locked and checked, but not kept.
    /// </summary>
    public bool Draining;

    /// <summary>
    /// The rows another request of the statement's transaction writes while
    /// the statement waits on its client, as the statement found them, which
    /// its reads take in their place (see <see cref="OwnWriteImages"/>); null
    /// until it first waits inside a transaction, and for a DML statement's
    /// <c>OUTPUT</c> rows.
    /// </summary>
    public OwnWriteImages? OwnWrites;

    /// <summary>The log <see cref="OwnWrites"/> watches, and the connection whose reads consult it.</summary>
    private UndoLog? watchedLog;

    private SimulatedDbConnection? watchingConnection;

    /// <summary>The request the statement belongs to, as it last suspended; null outside one.</summary>
    public SessionRequest? Request;

    /// <summary>
    /// For a DML statement's <c>OUTPUT</c> rows, the statement's own ending —
    /// its autocommit, or its place in the transaction — which waits for its
    /// last row: committed as the rows end, rolled back when they don't
    /// (<see cref="EndWrite"/>). Real can't suspend such a statement, so it
    /// writes as it sends and a cancel mid-way rolls it back (probed
    /// 2026-10-05 against SQL Server 2025); here it writes first and holds its
    /// ending. Null for a <c>SELECT</c>.
    /// </summary>
    public PendingStatementWrite? PendingWrite;

    /// <summary>The rows a DML statement changed, its <c>@@ROWCOUNT</c>; null for a <c>SELECT</c>, whose rows are its count.</summary>
    public int? AffectedRows;

    /// <summary>
    /// Set for a statement's own FOR JSON / FOR XML document, whose count —
    /// <c>@@ROWCOUNT</c> and its DONE token's — is the rows the clause
    /// serialized, not the chunks it sent (<see cref="StatementContext.ForClauseSourceRows"/>).
    /// </summary>
    public bool CountsForClauseSourceRows;

    /// <summary>The statement's count once it has produced its last row (see <see cref="CountsForClauseSourceRows"/>).</summary>
    public int StatementRowCount(BatchContext batch)
    {
        if (!this.CountsForClauseSourceRows)
            return this.AffectedRows ?? this.RowCount;
        var serialized = batch.CurrentStatement.ForClauseSourceRows;
        _ = this.Result?.ReportedRowCount = serialized;
        return serialized;
    }

    /// <summary>Ends a DML statement's <see cref="PendingWrite"/>, once: <paramref name="commit"/> or roll back.</summary>
    public void EndWrite(bool commit)
    {
        if (this.PendingWrite is not { } pending)
            return;
        this.PendingWrite = null;
        if (commit)
            pending.Complete();
        else
            pending.Rewind();
    }

    /// <summary>
    /// The consumer's way to have the statement produce more: advances the
    /// batch's outcome stream to the next <see cref="Marker"/>. Null until a
    /// consumer is positioned on the result.
    /// </summary>
    public Action? Pull;

    /// <summary>
    /// What the consumer does when the session ends under it with rows unread:
    /// disposes the outcome stream, which abandons the statement and gives
    /// back what it holds.
    /// </summary>
    public Action? AbandonByConsumer;

    /// <summary>The result set the rows belong to, which a row's error marks as cut short.</summary>
    public SimulatedSqlResultSet? Result;

    /// <summary>
    /// Whether the statement runs on a thread of its own (<see cref="StatementCoroutine"/>),
    /// which keeps the session reachable: its consumer is then named only
    /// weakly by <see cref="Pull"/> and <see cref="AbandonByConsumer"/>, so a
    /// consumer dropped without being closed can still be collected, which
    /// the statement's thread notices and ends the statement on.
    /// </summary>
    public readonly bool HoldsConsumerWeakly = StatementCoroutine.OnAnyStatementThread;

    /// <summary>The outcome the statement yields after each window it produces, which only the consumer reads.</summary>
    public static readonly SimulatedRowsProduced Marker = new();

    /// <summary>The bytes the client has been sent, as the TDS row writer would count them.</summary>
    private protected long producedBytes;

    /// <summary>The bytes of the rows the client has read, which the statement runs ahead of.</summary>
    private protected long clientBytes;

    /// <summary>
    /// How far the statement produces ahead of a client reading a row that
    /// begins <paramref name="read"/> bytes in. A client reading the rows
    /// itself has the five packets of the first window, then three more each
    /// time it reads into a third packet — the third, the sixth, the ninth —
    /// as real's statement refills (probed 2026-10-09 against SQL Server 2025:
    /// a reader of 2,007-byte rows held the keys of 20 rows two rows in, 32
    /// from its tenth, 44 from its twenty-first, 56 from its thirty-second,
    /// and 116 a hundred rows in; of 107-byte rows 373 until it read into the
    /// third packet, then 596, then 820 from its 380th). The MARS endpoint's
    /// writer has the statement a packet past the one it sends
    /// (<see cref="LeadPackets"/>).
    /// </summary>
    private protected long WindowEnd(long read) => this.LeadPackets == 1
        ? ((read / PacketBytes) + 1) * PacketBytes
        : (FirstWindowPackets + (RefillPackets * (((read / PacketBytes) + 1) / RefillPackets))) * PacketBytes;

    /// <summary>The packets a refill adds to the window (<see cref="WindowEnd"/>).</summary>
    private const int RefillPackets = 3;

    /// <summary>
    /// How many packets past the one its consumer is on the statement
    /// produces (<see cref="WindowEnd"/>): one for the MARS endpoint's writer,
    /// whose packets wait in the client's four-packet window beside it;
    /// otherwise the window steps as a client reading the rows itself reads.
    /// </summary>
    public int LeadPackets = FirstWindowPackets;

    /// <summary>
    /// Whether the session reported an executing thread as the statement
    /// suspended, which <see cref="Resume"/> restores as the resuming one.
    /// </summary>
    private bool hadThread;

    /// <summary>Whether the statement is suspended, waiting on its client.</summary>
    public bool Suspended;

    /// <summary>
    /// The statement's frame, whose <see cref="StatementContext.ProbedTable"/>
    /// names where a <c>READ COMMITTED</c> scan of it stands; null where none
    /// can.
    /// </summary>
    private protected StatementContext? statement;

    /// <summary>
    /// Whether the statement's last row came from a row its scan probed on the
    /// way to it — a scan streaming its rows, not a sort or aggregate that
    /// read them all first — so the scan stands on that row.
    /// </summary>
    private protected bool lastRowProbed;

    /// <summary>
    /// The statement's <see cref="StatementContext.RowsProbed"/> as its last
    /// row began: a table it probed since stands where that row's production
    /// left it (<see cref="StatementContext.OtherProbed"/>).
    /// </summary>
    private protected long lastRowProbesFrom;

    /// <summary>The S the suspended statement holds on the row each of its scans stands on (see <see cref="Suspend"/>).</summary>
    private List<LockResource>? positionLocks;

    private SessionToken? positionOwner;

    /// <summary>
    /// Produces the next window of rows into the buffer, or the rest
    /// when <see cref="Draining"/>; sets
    /// <see cref="Complete"/> once the source ends. An error a row raises ends
    /// the statement there, kept in <see cref="Error"/>.
    /// </summary>
    public void Produce()
    {
        var limit = this.Draining ? long.MaxValue : WindowEnd(this.clientBytes);
        try
        {
            this.ProduceUntil(limit);
        }
        catch (SimulatedSqlException error)
        {
            this.Error = error;
            this.Complete = true;
        }
        if (this.Complete)
        {
            this.DisposeSource();
            this.StopWatching();
        }
    }

    /// <summary>Stops <see cref="OwnWrites"/> noting writes and its reads consulting it, as the statement ends.</summary>
    private void StopWatching()
    {
        if (this.OwnWrites is not { } images)
            return;
        this.watchedLog?.Unwatch(images);
        this.watchedLog = null;
        if (this.watchingConnection is { } connection && ReferenceEquals(connection.ActiveOwnWriteImages, images))
            connection.ActiveOwnWriteImages = null;
    }

    /// <summary>
    /// Gives up the rows the statement hasn't produced: the client went away,
    /// or the statement failed — a DML statement's writes going back with them.
    /// </summary>
    public void Abandon()
    {
        this.Complete = true;
        this.DisposeSource();
        this.StopWatching();
        this.EndWrite(commit: false);
    }

    /// <summary>Lets go of the row S <see cref="Suspend"/> took, as the scan moves on or ends.</summary>
    private void ReleasePosition(SimulatedDbConnection connection)
    {
        if (this.positionLocks is not { Count: > 0 } held)
            return;
        foreach (var position in held)
            connection.Simulation.LockManager.Release(position, LockMode.Shared, this.positionOwner!);
        held.Clear();
        this.positionOwner = null;
    }

    private protected abstract void ProduceUntil(long limit);

    private protected abstract void DisposeSource();

    /// <summary>
    /// Waits on the client: the session runs nothing meanwhile, so it reports
    /// no executing thread — the same-thread deadlock check and the abandoned
    /// session sweep read that — its request is <c>suspended</c> on
    /// <c>ASYNC_NETWORK_IO</c>, and its <c>CommandTimeout</c> stops counting,
    /// as SqlClient's counts only the time a call spends waiting on the
    /// server. The statement keeps everything else it holds.
    /// </summary>
    public void Suspend(BatchContext batch)
    {
        var connection = batch.Connection;
        this.Suspended = true;
        batch.CurrentStatement.Suspensions++;
        this.hadThread = connection.CurrentExecutingThreadId is not null;
        connection.CurrentExecutingThreadId = null;
        connection.Session.WaitStartedTicks = Environment.TickCount64;
        this.Request = connection.ExecutingRequest;
        connection.Session.AwaitingClientRequest = connection.ExecutingRequest?.RequestId ?? 0;
        connection.PauseExecutionTimeout();
        // Inside a transaction, what another request of it writes while the
        // statement waits keeps out of the statement's reads.
        if (this.OwnWrites is null && this.PendingWrite is null && connection.CurrentTransaction is { } transaction)
        {
            this.OwnWrites = new OwnWriteImages();
            this.watchedLog = transaction.UndoLog;
            this.watchingConnection = connection;
            transaction.UndoLog.Watch(this.OwnWrites);
        }
        connection.ActiveOwnWriteImages = null;
        this.statementLocks = batch.StatementSchemaLocks;
        // A READ COMMITTED scan holds the row it stands on while it waits, and
        // so does every other scan the statement's last row came through.
        if (this.statement is { } frame)
        {
            if (this.lastRowProbed && frame.ProbedTable is { } table)
                this.HoldPosition(batch, table, frame.ProbedPage, frame.ProbedSlot);
            if (frame.OtherProbed is { Count: > 0 } others)
            {
                foreach (var other in others)
                {
                    if (other.Stamp > this.lastRowProbesFrom)
                        this.HoldPosition(batch, other.Table, other.Page, other.Slot);
                }
            }
        }
        var suspended = connection.SuspendedStreams ??= [];
        lock (suspended)
            suspended.Add(this);
    }

    /// <summary>Undoes <see cref="Suspend"/> as the client asks for more, on whichever thread it asks from.</summary>
    public void Resume(BatchContext batch)
    {
        if (!this.Suspended)
            return;
        this.Suspended = false;
        var connection = batch.Connection;
        this.ReleasePosition(connection);
        if (connection.SuspendedStreams is { } suspended)
        {
            lock (suspended)
                _ = suspended.Remove(this);
        }
        connection.Session.AwaitingClientRequest = -1;
        // Producing ahead of its client in the background, the statement is
        // timed only while its client waits on it (see AwaitBackground).
        if (this.worker is null)
            connection.ResumeExecutionTimeout();
        if (this.hadThread)
            connection.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
        connection.ActiveOwnWriteImages = this.OwnWrites;
        // Another request's statements ran meanwhile, publishing their own.
        DateOrder.Current = connection.DateFormat;
        Language.Current = connection.Language;
    }

    /// <summary>The statement-scoped locks of the statement as it last suspended, which a definition change of the transaction meets (<see cref="HoldsStatementLock"/>).</summary>
    private List<(LockResource Resource, LockMode Mode, SessionToken Owner)>? statementLocks;

    /// <summary>Whether the suspended statement holds a statement-scoped lock on <paramref name="resource"/> — the Sch-S every object it names takes.</summary>
    public bool HoldsStatementLock(LockResource resource)
    {
        if (!this.Suspended || this.statementLocks is not { } held)
            return false;
        foreach (var (locked, _, _) in held)
        {
            if (ReferenceEquals(locked, resource))
                return true;
        }
        return false;
    }

    private void HoldPosition(BatchContext batch, HeapTable table, int page, int slot)
    {
        if (batch.HoldScanPosition(table, page, slot) is not { } held)
            return;
        (this.positionLocks ??= []).Add(held);
        this.positionOwner = batch.Connection.LockOwner;
    }

    /// <summary>
    /// Ends the statement from outside its consumer — another session's
    /// <c>KILL</c> — when it is waiting on its client rather than running:
    /// real ends a request suspended on <c>ASYNC_NETWORK_IO</c> at once. A
    /// statement its consumer is running on is left to the cancellation the
    /// kill sends, which it meets at its next safe point.
    /// </summary>
    public void AbandonIfSuspended()
    {
        if (!this.Gate().TryEnter())
            return;
        try
        {
            if (this.Suspended && !this.Complete)
                this.AbandonByConsumer?.Invoke();
        }
        finally
        {
            this.Gate().Exit();
        }
    }

    /// <summary>
    /// Runs the consumer's <see cref="Pull"/>, holding the stream against an
    /// <see cref="AbandonIfSuspended"/> from another thread meanwhile, after
    /// handing the consumer what an earlier production in the background left
    /// unread, which comes ahead of anything this one produces.
    /// </summary>
    private protected void PullLocked(Action pull)
    {
        this.JoinBackground();
        _ = this.EndWorker();
        lock (this.Gate())
            pull();
    }

    /// <summary>The connection the statement runs on, for a stream its batch started; null otherwise.</summary>
    private protected SimulatedDbConnection? connection;

    /// <summary>The lock owner the statement started under, whose own holds can't keep it waiting.</summary>
    private protected SessionToken? owner;

    /// <summary>
    /// Set when the consumer frames the rows into packets itself — the TDS
    /// endpoint, whose client reads only the packets that went out — so every
    /// row produced ahead of a wait goes to it, the packet it ends in held back
    /// by its framing; the in-process reader reads only the rows that begin
    /// inside a packet the statement filled (see <see cref="PublishStaged"/>).
    /// </summary>
    public bool FramesPackets;

    /// <summary>
    /// Whether another session holds a lock that could keep the statement
    /// waiting (<see cref="Storage.LockManager.BlockingHolds"/>): then it
    /// produces its rows on a thread of its own while its client reads the
    /// ones it has, as real's server produces ahead of its client, so a wait
    /// on one row leaves the rows before it readable (see
    /// <see cref="AwaitBackground"/>). Uncontended, a production can't wait,
    /// so running it on the client's own call is the same.
    /// </summary>
    private protected bool Contended() =>
        this.connection is { } running && this.owner is { } own
        && Volatile.Read(ref running.Simulation.LockManager.BlockingHolds) > Volatile.Read(ref own.BlockingHolds);

    /// <summary>The production running on a thread of its own, null while none is; its thread's id.</summary>
    private Task? worker;

    private int workerThreadId;

    /// <summary>Set by the background production as it ends and as one of its lock waits begins.</summary>
    private ManualResetEventSlim? workerSignal;

    private volatile bool workerDone, waitBegan;

    /// <summary>What escaped the background production, for the consumer to meet.</summary>
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? workerFault;

    /// <summary>Whether the calling thread is the one producing in the background.</summary>
    internal bool OnWorker => Environment.CurrentManagedThreadId == Volatile.Read(ref this.workerThreadId);

    /// <summary>Whether a production in the background runs, or left rows or a fault the consumer hasn't had.</summary>
    private protected bool BackgroundPending => this.worker is not null || this.workerFault is not null || this.HasStaged;

    /// <summary>Whether rows produced in the background wait to be handed to the consumer.</summary>
    private protected abstract bool HasStaged { get; }

    /// <summary>Drops the rows produced in the background that a cancel kept from the consumer.</summary>
    private protected abstract void DiscardStaged();

    /// <summary>Starts taking the rows a production produces aside, for <see cref="PublishStaged"/> to hand over.</summary>
    private protected abstract void BeginStaging();

    /// <summary>
    /// Hands the consumer rows produced in the background: all of them with
    /// <paramref name="all"/> or <see cref="FramesPackets"/>, otherwise those
    /// beginning inside a packet the statement filled, which are what real's
    /// client reads while the statement waits (probed 2026-10-09 against SQL
    /// Server 2025, MARS or not: with 2,007-byte rows and a writer holding row
    /// 5, 9, 12, 30 or 100 the client read 4, 8, 8, 28 and 96 rows; with
    /// 107-byte rows and row 100, 151 or 301 held, 75, 150 and 299 — every
    /// row starting in an 8,000-byte packet sent, and nothing until the first
    /// was). Returns how many it handed over.
    /// </summary>
    private protected abstract int PublishStaged(bool all);

    /// <summary>
    /// Runs <paramref name="pull"/> on a thread of its own, the consumer going
    /// on with the rows it has; the consumer meets it again in
    /// <see cref="AwaitBackground"/> when it needs more, and every other entry
    /// into the session's engine waits it out first (<see cref="JoinBackground"/>).
    /// </summary>
    private protected void StartBackground(Action pull)
    {
        var running = this.connection!;
        var signal = this.workerSignal ??= new ManualResetEventSlim(false);
        signal.Reset();
        this.workerDone = this.waitBegan = false;
        this.BeginStaging();
        running.BackgroundProduction = this;
        var worker = new Task(
            () =>
            {
                Volatile.Write(ref this.workerThreadId, Environment.CurrentManagedThreadId);
                try
                {
                    using var culture = CultureScope.Engine();
                    lock (this.Gate())
                        pull();
                }
#pragma warning disable CA1031 // Whatever escapes the production is the consumer's to meet, rethrown on its own thread.
                catch (Exception fault)
#pragma warning restore CA1031
                {
                    this.workerFault = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault);
                }
                finally
                {
                    Volatile.Write(ref this.workerThreadId, 0);
                    if (ReferenceEquals(running.BackgroundProduction, this))
                        running.BackgroundProduction = null;
                    this.workerDone = true;
                    signal.Set();
                }
            },
            TaskCreationOptions.LongRunning);
        this.worker = worker;
        worker.Start(TaskScheduler.Default);
    }

    /// <summary>
    /// The background production's lock wait has begun, on its own thread
    /// (<see cref="Storage.LockManager"/>): the consumer waiting on it can have
    /// the rows produced before.
    /// </summary>
    internal void NoteWaitBegins()
    {
        if (!this.OnWorker || this.workerSignal is not { } signal)
            return;
        this.waitBegan = true;
        signal.Set();
    }

    /// <summary>
    /// The consumer needs rows the background production hasn't handed over:
    /// waits, counting toward its <c>CommandTimeout</c> as a client's wait on
    /// the server does, until the production ends or begins a lock wait with
    /// rows its client would have by then, and hands them over; a fault that
    /// escaped the production is thrown here.
    /// </summary>
    /// <returns>Whether it handed over any rows.</returns>
    private protected bool AwaitBackground()
    {
        if (this.worker is not null)
        {
            var signal = this.workerSignal!;
            this.connection!.ResumeExecutionTimeout();
            try
            {
                while (!this.workerDone)
                {
                    signal.Reset();
                    if (this.waitBegan)
                    {
                        this.waitBegan = false;
                        if (this.PublishStaged(all: false) != 0)
                            return true;
                    }
                    if (!this.workerDone)
                        signal.Wait();
                }
            }
            finally
            {
                this.connection.PauseExecutionTimeout();
            }
        }
        var handed = this.EndWorker();
        this.ThrowFault();
        return handed;
    }

    /// <summary>
    /// Ends a background production that has ended — none runs any longer
    /// unless <see cref="workerDone"/> says otherwise — handing over what it
    /// produced: all of it, unless a cancel ended the statement, which ends
    /// it where its client stands, the rows in a packet it never filled going
    /// nowhere. Whether it handed over any rows.
    /// </summary>
    private bool EndWorker()
    {
        if (this.worker is { } running)
        {
            if (!this.workerDone)
                return false;
            running.Wait();
            this.worker = null;
        }
        var handed = this.PublishStaged(all: this.Error is not { Number: 0 or -2 }) != 0;
        this.DiscardStaged();
        return handed;
    }

    /// <summary>
    /// <see cref="EndWorker"/> for the consumer, between two rows it reads:
    /// whether no production runs in the background any longer.
    /// </summary>
    private protected bool ReapBackground()
    {
        if (this.worker is not null && !this.workerDone)
            return false;
        _ = this.EndWorker();
        this.ThrowFault();
        return true;
    }

    private void ThrowFault()
    {
        if (this.workerFault is not { } fault)
            return;
        this.workerFault = null;
        fault.Throw();
    }

    /// <summary>
    /// Waits out the background production, if one runs and the caller isn't
    /// it, before anything else of the session's engine runs: the rows it
    /// produced and a fault it met stay for the consumer. The wait counts
    /// toward the statement's <c>CommandTimeout</c>, so a lock wait with no
    /// end of its own can't hold the caller for good.
    /// </summary>
    internal void JoinBackground()
    {
        if (this.worker is not { } running || this.workerDone || this.OnWorker)
            return;
        var waiting = this.connection!;
        waiting.ResumeExecutionTimeout();
        try
        {
            running.Wait();
        }
        finally
        {
            waiting.PauseExecutionTimeout();
        }
    }

    /// <summary>
    /// Held while the consumer runs the statement on, which a
    /// <see cref="AbandonIfSuspended"/> from another thread defers to; created
    /// with the first, since most results complete in their first window.
    /// </summary>
    private Lock? gate;

    private Lock Gate() => LazyInitializer.EnsureInitialized(ref this.gate);

    /// <summary>
    /// The first window: what the statement produces before its client reads
    /// anything — the packet the client is reading and the four ahead of it.
    /// </summary>
    private protected const long FirstWindowBytes = FirstWindowPackets * PacketBytes;

    private const int FirstWindowPackets = 5;

    /// <summary>
    /// An estimate of the bytes a column metadata token takes, which the first
    /// window counts ahead of the rows.
    /// </summary>
    private protected static long HeaderBytes(string[] names)
    {
        long total = 8;
        foreach (var name in names)
            total += 8 + (2 * name.Length);
        return total;
    }
}

/// <summary>
/// A <see cref="ResultStream"/> over rows of one form — projected
/// <see cref="SqlValue"/> rows or a niche producer's encoded ones — measured
/// by <typeparamref name="TMeasure"/> as the wire would carry them.
/// </summary>
internal sealed class ResultStream<T, TMeasure> : ResultStream
    where T : class
    where TMeasure : IRowMeasure<T>
{
    private readonly IEnumerator<T> source;
    private readonly TMeasure measure;

    /// <summary>
    /// Rows produced and not yet read: <see cref="Head"/> is the next one the
    /// consumer reads, and the slots before it are cleared as they are read.
    /// </summary>
    public readonly List<T> Buffer;

    /// <summary>The next row of <see cref="Buffer"/> the consumer reads.</summary>
    public int Head;

    private ResultStream(IEnumerator<T> source, TMeasure measure, List<T> buffer, long headerBytes)
    {
        this.source = source;
        this.measure = measure;
        this.Buffer = buffer;
        this.clientBytes = headerBytes;
        this.producedBytes = headerBytes;
        this.nextPacket = ((headerBytes / PacketBytes) + 1) * PacketBytes;
        foreach (var row in buffer)
            this.Produced(row);
        this.RowCount = buffer.Count;
    }

    /// <summary>
    /// The rows, counted from the result's first, whose last byte lies in a
    /// packet after the row before's, with the bytes produced through them:
    /// reading one moves the client into that packet, the statement running on
    /// by as much (<see cref="Rows"/>) — so a row costs the reader no measure.
    /// </summary>
    private readonly Queue<(long Row, long Bytes)> packetEnds = new();

    /// <summary>The rows the consumer has read, counted from the result's first.</summary>
    private long rowsRead;

    /// <summary>Counts <paramref name="row"/>'s bytes as produced, noting the packet it ends in.</summary>
    private void Produced(T row)
    {
        this.producedBytes += this.measure.Of(row);
        this.producedRows++;
        if (this.producedBytes >= this.nextPacket)
        {
            this.packetEnds.Enqueue((this.producedRows, this.producedBytes));
            this.nextPacket = ((this.producedBytes / PacketBytes) + 1) * PacketBytes;
        }
    }

    private long producedRows;

    /// <summary>Where the packet after the one the last row produced ends in begins.</summary>
    private long nextPacket;

    /// <summary>
    /// Produces <paramref name="rows"/>' first window — what real sends before
    /// its client reads anything — into <paramref name="produced"/>, which
    /// holds every row when the source ends inside it, the statement then
    /// done (null returned), and otherwise the window, the stream returned
    /// reading on from there. A row's error leaves the rows before it in
    /// <paramref name="produced"/> and propagates.
    /// </summary>
    /// <remarks>
    /// Under contention for the locks the statement reads under
    /// (<see cref="ResultStream.Contended"/>), the window is the first packet
    /// alone, which is what real's client has once the statement's first
    /// packet is full: the rest it produces in the background as its client
    /// reads.
    /// </remarks>
    public static ResultStream<T, TMeasure>? Start(IEnumerable<T> rows, string[] names, TMeasure measure, StatementContext? statement, SimulatedDbConnection? connection, out List<T> produced)
    {
        produced = [];
        var source = rows.GetEnumerator();
        var header = HeaderBytes(names);
        var bytes = header;
        var owner = connection?.LockOwner;
        var contended = owner is not null && Volatile.Read(ref connection!.Simulation.LockManager.BlockingHolds) > Volatile.Read(ref owner.BlockingHolds);
        var window = contended ? PacketBytes : FirstWindowBytes;
        try
        {
            var probed = false;
            long probesFrom = 0;
            while (bytes < window)
            {
                var probes = statement?.RowsProbed ?? 0;
                if (!source.MoveNext())
                {
                    source.Dispose();
                    return null;
                }
                probed = statement is not null && statement.RowsProbed != probes;
                probesFrom = probes;
                var row = source.Current;
                produced.Add(row);
                bytes += measure.Of(row);
            }
            return new ResultStream<T, TMeasure>(source, measure, produced, header)
            {
                statement = statement,
                lastRowProbed = probed,
                lastRowProbesFrom = probesFrom,
                connection = connection,
                owner = owner,
                producesAhead = contended,
            };
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Whether the statement began under contention, so produces ahead of its client in the background from its first read.</summary>
    private bool producesAhead;

    /// <summary>
    /// Rows the background production produced that the consumer hasn't been
    /// handed (<see cref="PublishStaged"/>), guarded by itself, with their
    /// bytes; null until a production first runs in the background.
    /// </summary>
    private List<T>? staging;

    private long stagedBytes;

    private protected override bool HasStaged => this.staging is { Count: > 0 };

    private protected override void BeginStaging() => this.staging ??= [];

    private protected override void DiscardStaged()
    {
        if (this.staging is not { } staged)
            return;
        lock (staged)
        {
            staged.Clear();
            this.stagedBytes = 0;
        }
    }

    private protected override int PublishStaged(bool all)
    {
        if (this.staging is not { } staged)
            return 0;
        lock (staged)
        {
            var boundary = all || this.FramesPackets ? long.MaxValue : (this.producedBytes + this.stagedBytes) / PacketBytes * PacketBytes;
            var moved = 0;
            for (; moved < staged.Count && this.producedBytes < boundary; moved++)
            {
                var row = staged[moved];
                var before = this.producedBytes;
                this.Produced(row);
                this.stagedBytes -= this.producedBytes - before;
                this.Buffer.Add(row);
            }
            staged.RemoveRange(0, moved);
            return moved;
        }
    }

    /// <summary>
    /// <see cref="ProduceUntil"/> on the background production's thread: the
    /// rows go aside for <see cref="PublishStaged"/>, the consumer reading
    /// from <see cref="Buffer"/> meanwhile.
    /// </summary>
    private void ProduceStaged(long limit, List<T> staged)
    {
        var source = this.source;
        var statement = this.statement;
        while (true)
        {
            lock (staged)
            {
                if (this.producedBytes + this.stagedBytes >= limit)
                    return;
            }
            var probes = statement?.RowsProbed ?? 0;
            if (!source.MoveNext())
            {
                this.Complete = true;
                return;
            }
            this.lastRowProbed = statement is not null && statement.RowsProbed != probes;
            this.lastRowProbesFrom = probes;
            var row = source.Current;
            var size = this.measure.Of(row);
            lock (staged)
            {
                this.RowCount++;
                staged.Add(row);
                this.stagedBytes += size;
            }
        }
    }

    private protected override void ProduceUntil(long limit)
    {
        if (this.OnWorker && this.staging is { } staged)
        {
            this.ProduceStaged(limit, staged);
            return;
        }
        var buffer = this.Buffer;
        if (this.Head == buffer.Count)
        {
            buffer.Clear();
            this.Head = 0;
        }
        var source = this.source;
        var statement = this.statement;
        while (this.producedBytes < limit)
        {
            var probes = statement?.RowsProbed ?? 0;
            if (!source.MoveNext())
            {
                this.Complete = true;
                return;
            }
            this.lastRowProbed = statement is not null && statement.RowsProbed != probes;
            this.lastRowProbesFrom = probes;
            var row = source.Current;
            this.RowCount++;
            if (this.Draining)
            {
                this.producedBytes += this.measure.Of(row);
                continue;
            }
            this.Produced(row);
            buffer.Add(row);
        }
    }

    private protected override void DisposeSource() => this.source.Dispose();

    /// <summary>
    /// Has the statement produce ahead of its client in the background as far
    /// as its window reaches, once any production there before has ended and
    /// handed over what it produced.
    /// </summary>
    private void RunAhead()
    {
        if (!this.ReapBackground())
            return;
        if (!this.Complete && !this.Draining && this.Pull is { } pull && this.producedBytes < WindowEnd(this.clientBytes))
            this.StartBackground(pull);
    }

    /// <summary>
    /// The rows as the consumer reads them, the statement running on as the
    /// client reads into each next packet (<see cref="ResultStream.WindowEnd"/>),
    /// and whenever the buffer runs dry.
    /// </summary>
    public IEnumerable<T> Rows()
    {
        try
        {
            // Begun under contention, the statement goes on producing ahead of
            // its client at once, as real's does past its first packet.
            if (this.producesAhead)
                this.RunAhead();
            while (true)
            {
                if (this.Head < this.Buffer.Count)
                {
                    var row = this.Buffer[this.Head];
                    this.Buffer[this.Head++] = null!;
                    // Into another packet, the client lets the statement run on:
                    // the endpoint's writer as it sends the row a packet ends
                    // in, a reader as it reads the first row beginning past it.
                    this.rowsRead++;
                    if (this.packetEnds.TryPeek(out var end) && end.Row == (this.LeadPackets == 1 ? this.rowsRead : this.rowsRead - 1))
                    {
                        _ = this.packetEnds.Dequeue();
                        this.clientBytes = end.Bytes;
                        if (!this.Complete && !this.Draining && this.Pull is { } refill)
                        {
                            // Most of the buffer read, it starts again from the front.
                            if (this.Head >= 64 && this.Head * 2 >= this.Buffer.Count)
                            {
                                this.Buffer.RemoveRange(0, this.Head);
                                this.Head = 0;
                            }
                            if (this.BackgroundPending || this.Contended())
                                this.RunAhead();
                            else if (this.producedBytes < WindowEnd(this.clientBytes))
                                this.PullLocked(refill);
                        }
                    }
                    yield return row;
                    continue;
                }
                if (this.BackgroundPending)
                {
                    // Nothing more to hand over from a production that has
                    // ended without the statement's: the consumer can no
                    // longer run it on.
                    if (!this.AwaitBackground() && !this.Complete && !this.BackgroundPending)
                        yield break;
                    continue;
                }
                if (this.Complete || this.Pull is not { } pull)
                    yield break;
                // A reader that has read every row produced stands where the
                // next one begins, however far into it the last one reached.
                if (this.LeadPackets != 1)
                    this.clientBytes = Math.Max(this.clientBytes, this.producedBytes);
                if (!this.Draining && this.Contended())
                {
                    this.StartBackground(pull);
                    continue;
                }
                this.PullLocked(pull);
                // A consumer that can no longer run the statement on — its
                // reader closed under it — ends the rows where they stand.
                if (this.Head == this.Buffer.Count && !this.Complete)
                    yield break;
            }
        }
        finally
        {
            // A consumer that stops reading — a reader moving on or closing —
            // leaves the rest to be produced and discarded.
            if (!this.Complete)
                this.Draining = true;
        }
    }
}

/// <summary>How a <see cref="ResultStream"/>'s rows measure on the wire.</summary>
internal interface IRowMeasure<in T>
{
    /// <summary>Roughly how many bytes <paramref name="row"/> takes as TDS sends it.</summary>
    long Of(T row);
}

/// <summary>
/// How a schema's projected rows measure as TDS sends them: a token byte,
/// each fixed-length column's width with the length prefix its wire family
/// carries — none for a NOT NULL integer, float, money or <c>datetime</c> —
/// and each other column's value with its length, a character
/// column's characters at the width its family sends them. A struct over a
/// shape kept per plan, which a cached plan shares across its executions, so
/// the measure costs no allocation and its calls specialize.
/// </summary>
internal readonly struct ValueRowMeasure : IRowMeasure<SqlValue[]>
{
    /// <summary>Keyed by the plan's nullability array where it has one, else its schema: both are a plan's own.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Shape> shapes = [];

    private readonly Shape shape;

    private ValueRowMeasure(Shape shape) => this.shape = shape;

    public static ValueRowMeasure For(SqlType[] schema, bool[]? nullability)
    {
        object key = nullability is null ? schema : nullability;
        if (shapes.TryGetValue(key, out var known))
            return new(known);
        long fixedBytes = 1;
        var variable = new List<int>();
        for (var i = 0; i < schema.Length; i++)
        {
            if (schema[i].IsFixedLength)
                fixedBytes += schema[i].FixedLength + Network.TdsTypeCodec.FixedValuePrefix(schema[i], notNull: nullability is not null && !nullability[i]);
            else
                variable.Add(SqlType.IsNationalStringCategory(schema[i]) ? ~i : i);
        }
        var shape = new Shape(fixedBytes, [.. variable]);
        _ = shapes.TryAdd(key, shape);
        return new(shape);
    }

    public long Of(SqlValue[] row)
    {
        var total = this.shape.FixedBytes;
        foreach (var column in this.shape.Variable)
        {
            total += column >= 0
                ? row[column].ClientWireEstimate(national: false)
                : row[~column].ClientWireEstimate(national: true);
        }
        return total;
    }

    /// <summary>
    /// The token byte and every fixed-length column's width — what each row
    /// takes before its variable-length columns — and the variable-length
    /// columns' ordinals, a national one's complemented (<c>~ordinal</c>).
    /// </summary>
    private sealed class Shape(long fixedBytes, int[] variable)
    {
        public readonly long FixedBytes = fixedBytes;
        public readonly int[] Variable = variable;
    }
}

/// <summary>How a niche producer's encoded rows measure: by their page image.</summary>
internal readonly struct EncodedRowMeasure : IRowMeasure<byte[]>
{
    public long Of(byte[] row) => 1 + row.Length;
}

/// <summary>
/// The ending a DML statement holds while its <c>OUTPUT</c> rows go out
/// (<see cref="ResultStream.PendingWrite"/>): what completes it, and what rolls
/// it back.
/// </summary>
internal sealed class PendingStatementWrite(Action complete, Action rewind)
{
    public readonly Action Complete = complete;
    public readonly Action Rewind = rewind;
}
