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
    /// Set when the statement has to finish before something else runs on the
    /// session — another request's write in the same transaction (see
    /// <see cref="SimulatedDbConnection.FinishReadsBeforeWrite"/>): the rest of
    /// the rows are produced into the buffer at once.
    /// </summary>
    public bool Unbounded;

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

    /// <summary>The outcome the statement yields after each window it produces, which only the consumer reads.</summary>
    public static readonly SimulatedRowsProduced Marker = new();

    /// <summary>The bytes the client has been sent, as the TDS row writer would count them.</summary>
    private protected long producedBytes;

    /// <summary>The bytes of the rows the client has read, which the statement runs ahead of.</summary>
    private protected long clientBytes;

    /// <summary>
    /// How far the statement produces ahead of a client that has read
    /// <paramref name="read"/> bytes: through the packet the client is
    /// reading and the four after it (<see cref="LeadPackets"/>). Real refills its window as the client
    /// reads each packet (probed 2026-10-08 against SQL Server 2025: a
    /// reader 2, 50, 100 and 300 rows into a result of 2,007-byte rows held
    /// the locks of 20, 68, 116 and 318).
    /// </summary>
    private protected long WindowEnd(long read) => ((read / PacketBytes) + this.LeadPackets) * PacketBytes;

    /// <summary>
    /// How many packets past the one its consumer is on the statement
    /// produces (<see cref="WindowEnd"/>): five for a client reading the rows
    /// itself; one for the MARS endpoint's writer, whose packets wait in the
    /// client's four-packet window beside it.
    /// </summary>
    public int LeadPackets = 5;

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

    /// <summary>The S the suspended statement holds on the row its scan stands on (see <see cref="Suspend"/>).</summary>
    private LockResource? positionLock;

    private SessionToken? positionOwner;

    /// <summary>
    /// Produces the next window of rows into the buffer, or the rest
    /// when <see cref="Draining"/> or <see cref="Unbounded"/>; sets
    /// <see cref="Complete"/> once the source ends. An error a row raises ends
    /// the statement there, kept in <see cref="Error"/>.
    /// </summary>
    public void Produce()
    {
        var limit = this.Draining || this.Unbounded ? long.MaxValue : WindowEnd(this.clientBytes);
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
            this.DisposeSource();
    }

    /// <summary>
    /// Gives up the rows the statement hasn't produced: the client went away,
    /// or the statement failed — a DML statement's writes going back with them.
    /// </summary>
    public void Abandon()
    {
        this.Complete = true;
        this.DisposeSource();
        this.EndWrite(commit: false);
    }

    /// <summary>Lets go of the row S <see cref="Suspend"/> took, as the scan moves on or ends.</summary>
    private void ReleasePosition(SimulatedDbConnection connection)
    {
        if (this.positionLock is not { } held)
            return;
        this.positionLock = null;
        connection.Simulation.LockManager.Release(held, LockMode.Shared, this.positionOwner!);
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
        // A READ COMMITTED scan holds the row it stands on while it waits.
        if (this.lastRowProbed && this.statement is { ProbedTable: { } table } frame)
        {
            this.positionLock = batch.HoldScanPosition(table, frame.ProbedPage, frame.ProbedSlot);
            this.positionOwner = this.positionLock is null ? null : connection.LockOwner;
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
        connection.ResumeExecutionTimeout();
        if (this.hadThread)
            connection.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
        // Another request's statements ran meanwhile, publishing their own.
        DateOrder.Current = connection.DateFormat;
        Language.Current = connection.Language;
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
    /// <see cref="AbandonIfSuspended"/> from another thread meanwhile.
    /// </summary>
    private protected void PullLocked(Action pull)
    {
        lock (this.Gate())
            pull();
    }

    /// <summary>
    /// Held while the consumer runs the statement on, which a
    /// <see cref="AbandonIfSuspended"/> from another thread defers to; created
    /// with the first, since most results complete in their first window.
    /// </summary>
    private Lock? gate;

    private Lock Gate() => LazyInitializer.EnsureInitialized(ref this.gate);

    /// <summary>
    /// Runs the statement to its end on the consumer's behalf, keeping the
    /// rest of its rows for the consumer to read later.
    /// </summary>
    public void Finish()
    {
        this.Unbounded = true;
        while (!this.Complete && this.Pull is { } pull)
            this.PullLocked(pull);
    }

    /// <summary>
    /// The first window: what the statement produces before its client reads
    /// anything — the packet the client is reading and the four ahead of it.
    /// </summary>
    private protected const long FirstWindowBytes = 5 * PacketBytes;

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
    public static ResultStream<T, TMeasure>? Start(IEnumerable<T> rows, string[] names, TMeasure measure, StatementContext? statement, out List<T> produced)
    {
        produced = [];
        var source = rows.GetEnumerator();
        var header = HeaderBytes(names);
        var bytes = header;
        try
        {
            var probed = false;
            while (bytes < FirstWindowBytes)
            {
                var probes = statement?.RowsProbed ?? 0;
                if (!source.MoveNext())
                {
                    source.Dispose();
                    return null;
                }
                probed = statement is not null && statement.RowsProbed != probes;
                var row = source.Current;
                produced.Add(row);
                bytes += measure.Of(row);
            }
            return new ResultStream<T, TMeasure>(source, measure, produced, header) { statement = statement, lastRowProbed = probed };
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private protected override void ProduceUntil(long limit)
    {
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
    /// The rows as the consumer reads them, the statement running on as the
    /// client reads into each next packet (<see cref="ResultStream.WindowEnd"/>),
    /// and whenever the buffer runs dry.
    /// </summary>
    public IEnumerable<T> Rows()
    {
        try
        {
            while (true)
            {
                if (this.Head < this.Buffer.Count)
                {
                    var row = this.Buffer[this.Head];
                    this.Buffer[this.Head++] = null!;
                    // Into another packet, the client lets the statement run on.
                    this.rowsRead++;
                    if (this.packetEnds.TryPeek(out var end) && end.Row == this.rowsRead)
                    {
                        _ = this.packetEnds.Dequeue();
                        this.clientBytes = end.Bytes;
                        if (!this.Complete && !this.Draining && this.producedBytes < WindowEnd(this.clientBytes) && this.Pull is { } refill)
                        {
                            // Most of the buffer read, it starts again from the front.
                            if (this.Head >= 64 && this.Head * 2 >= this.Buffer.Count)
                            {
                                this.Buffer.RemoveRange(0, this.Head);
                                this.Head = 0;
                            }
                            this.PullLocked(refill);
                        }
                    }
                    yield return row;
                    continue;
                }
                if (this.Complete || this.Pull is not { } pull)
                    yield break;
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
/// each fixed-length column's width — a length byte more where it is
/// nullable — and each other column's value with its length, a character
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
                fixedBytes += schema[i].FixedLength + (nullability is not null && !nullability[i] ? 0 : 1);
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
