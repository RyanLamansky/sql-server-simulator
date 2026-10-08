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
    private const int PacketBytes = 8000;

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
    /// session — another request in process, which is run whole-statement
    /// (see <see cref="SimulatedDbConnection.FinishSuspendedStreams"/>): the
    /// rest of the rows are produced into the buffer at once.
    /// </summary>
    public bool Unbounded;

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

    /// <summary>
    /// Whether the session reported an executing thread as the statement
    /// suspended, which <see cref="Resume"/> restores as the resuming one.
    /// </summary>
    private bool hadThread;

    /// <summary>Whether the statement is suspended, waiting on its client.</summary>
    public bool Suspended;

    /// <summary>
    /// Produces the next window of rows into the buffer, or the rest
    /// when <see cref="Draining"/> or <see cref="Unbounded"/>; sets
    /// <see cref="Complete"/> once the source ends. An error a row raises ends
    /// the statement there, kept in <see cref="Error"/>.
    /// </summary>
    public void Produce()
    {
        var limit = this.Draining || this.Unbounded
            ? long.MaxValue
            : ((this.producedBytes / PacketBytes) + 5) * PacketBytes;
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

    /// <summary>Gives up the rows the statement hasn't produced: the client went away.</summary>
    public void Abandon()
    {
        this.Complete = true;
        this.DisposeSource();
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
        this.hadThread = connection.CurrentExecutingThreadId is not null;
        connection.CurrentExecutingThreadId = null;
        connection.Session.WaitStartedTicks = Environment.TickCount64;
        connection.Session.AwaitingClientRequest = connection.ExecutingRequest?.RequestId ?? 0;
        connection.PauseExecutionTimeout();
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
        if (connection.SuspendedStreams is { } suspended)
        {
            lock (suspended)
                _ = suspended.Remove(this);
        }
        connection.Session.AwaitingClientRequest = -1;
        connection.ResumeExecutionTimeout();
        if (this.hadThread)
            connection.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
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

    private ResultStream(IEnumerator<T> source, TMeasure measure, List<T> buffer, long producedBytes)
    {
        this.source = source;
        this.measure = measure;
        this.Buffer = buffer;
        this.producedBytes = producedBytes;
        this.RowCount = buffer.Count;
    }

    /// <summary>
    /// Produces <paramref name="rows"/>' first window — what real sends before
    /// its client reads anything — into <paramref name="produced"/>, which
    /// holds every row when the source ends inside it, the statement then
    /// done (null returned), and otherwise the window, the stream returned
    /// reading on from there. A row's error leaves the rows before it in
    /// <paramref name="produced"/> and propagates.
    /// </summary>
    public static ResultStream<T, TMeasure>? Start(IEnumerable<T> rows, string[] names, TMeasure measure, out List<T> produced)
    {
        produced = [];
        var source = rows.GetEnumerator();
        var bytes = HeaderBytes(names);
        try
        {
            while (bytes < FirstWindowBytes)
            {
                if (!source.MoveNext())
                {
                    source.Dispose();
                    return null;
                }
                var row = source.Current;
                produced.Add(row);
                bytes += measure.Of(row);
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }
        return new ResultStream<T, TMeasure>(source, measure, produced, bytes);
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
        while (this.producedBytes < limit)
        {
            if (!source.MoveNext())
            {
                this.Complete = true;
                return;
            }
            var row = source.Current;
            this.RowCount++;
            this.producedBytes += this.measure.Of(row);
            if (!this.Draining)
                buffer.Add(row);
        }
    }

    private protected override void DisposeSource() => this.source.Dispose();

    /// <summary>The rows as the consumer reads them, pulling another window whenever the buffer runs dry.</summary>
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
