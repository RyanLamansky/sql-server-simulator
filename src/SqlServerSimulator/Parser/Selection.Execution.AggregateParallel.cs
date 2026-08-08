using System.Collections.Concurrent;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Parallel grouped accumulation: the consumer half of the streaming
/// single-grouping-set aggregate path, run on worker threads while the row
/// stream itself keeps being produced serially by the calling thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the partition boundary is, and why.</b> Real runs these shapes at
/// DOP 8 and the simulator runs them on one core, which is the systematic
/// remainder behind the surviving scan-bound ratios. The obvious partition —
/// chunks of a heap's page list — reaches only a single-table scan, and the
/// shapes that actually cost are two-table joins, where partitioning the
/// driving side would multiply the hash build by the degree of parallelism.
/// So the boundary sits one level up: <see cref="EnumerateJoinedRows"/> is
/// unchanged and runs on the calling thread (the <em>producer</em>), and what
/// forks is the per-row consumer pipeline — the WHERE excluders, the
/// grouping-key evaluation, the aggregate operands, and the column decoding
/// all three of those trigger. One mechanism then covers a single-table scan
/// and a join alike, and neither the join driver nor the heap is touched.
/// </para>
/// <para>
/// <b>What that buys, and what bounds it.</b> The ceiling is Amdahl over the
/// producer: a scan whose consumer does real work (a filtered aggregate, a
/// conditional pivot) parallelizes well, while a query whose cost is the join
/// build itself does not. That is a measured property, not an assumed one.
/// </para>
/// <para>
/// <b>Locking and MVCC are untouched.</b> Every lock probe, snapshot-Xid
/// allocation and version-store read lives inside
/// <see cref="BatchContext.WrapWithRowConflictChecks"/>, which is part of the
/// enumerator and therefore runs on the producer thread only. Workers never
/// reach the lock manager or the version store, so isolation level, lock
/// footprint and acquisition order are byte-identical to the serial path and
/// no isolation gate is needed. Row bytes are safe to hand across threads
/// because <c>HeapPage.TryReadLiveSlot</c> already returns a private copy of
/// every scanned row.
/// </para>
/// <para>
/// <b>Error identity.</b> On any exception — in a worker, or in the producer
/// once workers exist — the attempt is cancelled, every partial group is
/// discarded, and the statement is re-run serially from scratch. The serial
/// re-run is the canonical row order and raises the canonical error, so the
/// probe-pinned streaming semantic (an aggregate operand raising on an early
/// row preempts a WHERE that would have raised on a later one) survives
/// verbatim. Errors are rare and the re-run is only ever a second read.
/// </para>
/// <para>
/// <b>Serial order stays observable where it is observable.</b> Blocks are
/// dispatched round-robin to fixed per-worker queues rather than to whichever
/// worker is free, so each worker sees rows in increasing order; the merge
/// then keeps the lower <see cref="GroupState.FirstRowOrdinal"/> group's key
/// tuple and representative row. That reproduces the serial answer for the
/// cases where a group's identity is coarser than its rendering — a
/// case-insensitive collation buckets <c>'abc'</c> with <c>'ABC'</c> and
/// projects whichever arrived first. What ordinals cannot settle (a
/// <c>MIN</c> / <c>MAX</c> tie between values that render differently) is
/// declined by <see cref="Aggregator.TryMergeFrom"/> and re-runs serially.
/// </para>
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>
    /// How many input rows the serial path accumulates before it considers
    /// forking. Deciding mid-stream rather than from a cardinality estimate
    /// means no estimate has to exist and a small query never pays the fork;
    /// the rows read before the switch are already in the coordinator's own
    /// group map and simply take part in the merge.
    /// </summary>
    private const int ParallelRowThreshold = 16_384;

    /// <summary>
    /// The reciprocal of the share of prefix rows that may have created a
    /// group before the fork is judged not worth taking. Every worker builds
    /// its own group map and the coordinator folds them all in afterwards, so
    /// a query whose groups are nearly as numerous as its rows pays that merge
    /// on almost every row and wins nothing — measured as a regression on a
    /// 231k-row <c>GROUP BY OrderID</c> over 73k groups. The prefix has
    /// already been accumulated by the time the decision is taken, so its own
    /// group count is the estimate, at no cost.
    /// </summary>
    private const int ParallelGroupDensityDivisor = 8;

    /// <summary>
    /// Rows per hand-off block. Large enough that the per-block queue traffic
    /// disappears against the per-row work, small enough that a worker's queue
    /// holds a bounded slice of the stream.
    /// </summary>
    private const int ParallelBlockRows = 1_024;

    /// <summary>Blocks in flight per worker (one being filled, one queued, one being drained).</summary>
    private const int ParallelBlocksPerWorker = 3;

    /// <summary>Upper bound on worker threads for one statement, whatever the host's core count.</summary>
    private const int ParallelMaxWorkers = 8;

    /// <summary>
    /// Process-wide ceiling on worker threads across every concurrently
    /// executing statement. Without it, many sessions each forking their own
    /// set oversubscribes the machine and — worse — the second and later
    /// statements wait on threads the first is using. A statement that cannot
    /// reserve at least two slots runs serially, which is always a correct
    /// outcome, so the budget needs no fairness beyond first-come.
    /// </summary>
    private static readonly int ParallelWorkerBudget = Environment.ProcessorCount;

    private static int outstandingParallelWorkers;

    /// <summary>
    /// Claims up to <paramref name="wanted"/> worker slots from the
    /// process-wide budget, returning how many were granted (possibly zero).
    /// </summary>
    private static int ReserveParallelWorkers(int wanted)
    {
        while (true)
        {
            var current = Volatile.Read(ref outstandingParallelWorkers);
            var granted = Math.Min(wanted, ParallelWorkerBudget - current);
            if (granted <= 0)
                return 0;
            if (Interlocked.CompareExchange(ref outstandingParallelWorkers, current + granted, current) == current)
                return granted;
        }
    }

    /// <summary>
    /// The threads the fan-out runs on: a shared pool that grows to
    /// <see cref="ParallelWorkerBudget"/> and retires threads that go unused.
    /// <para>
    /// Neither of the obvious alternatives works. A thread per worker per
    /// statement measured as a throughput <em>regression</em> on the concurrent
    /// workload driver, because creating and tearing down eight OS threads per
    /// forking statement costs more than the fan-out saves. The .NET thread
    /// pool can't serve either — the producer blocks waiting for a worker, and
    /// the caller is often itself on a pool thread, so a saturated pool would
    /// stall the producer behind workers it hasn't started.
    /// </para>
    /// <para>
    /// Starvation-free by the budget: it caps outstanding workers at the same
    /// number, so every submitted run reaches a thread — either a parked one, a
    /// newly grown one, or one of the busy threads that must be about to
    /// finish, since a full budget means the whole reservation is executing.
    /// </para>
    /// </summary>
    private static class ParallelWorkerPool
    {
        /// <summary>
        /// How long an unused worker thread waits before retiring. Long enough
        /// that a run of analytical statements keeps its threads warm, short
        /// enough that an engine which forked once and then went back to short
        /// transactional statements doesn't carry the threads around: every
        /// live thread is one more for the garbage collector to suspend and
        /// scan at each collection, which measured as the dominant cost of a
        /// fan-out the concurrent workload never repeated.
        /// </summary>
        private const int IdleTimeoutMilliseconds = 5_000;

        private static readonly object Gate = new();

#pragma warning disable SSS008 // Deliberate: the hand-off queue is mutated for the life of the process, which is what the rule's "fixed after initialization" premise excludes.
        private static readonly Queue<Action> Pending = new();
#pragma warning restore SSS008

        private static int threads;
        private static int idle;

        public static void Submit(Action run)
        {
            lock (Gate)
            {
                Pending.Enqueue(run);
                if (idle > 0)
                {
                    // A parked thread takes it.
                    Monitor.Pulse(Gate);
                    return;
                }

                if (threads < ParallelWorkerBudget)
                {
                    threads++;
                    new Thread(Loop)
                    {
                        IsBackground = true,
                        Name = "SqlServerSimulator aggregate worker",
                    }.Start();
                    return;
                }

                // At the budget with every thread busy. That can only happen
                // while `ParallelWorkerBudget` runs are executing, which is the
                // whole reservation, so one of them is about to finish and loop
                // back for this item.
            }
        }

        private static void Loop()
        {
            while (true)
            {
                Action run;
                lock (Gate)
                {
                    while (Pending.Count == 0)
                    {
                        idle++;
                        var signalled = Monitor.Wait(Gate, IdleTimeoutMilliseconds);
                        idle--;
                        // Retire only with the queue observed empty under the
                        // same lock the submitter takes, so an item can never
                        // be stranded by a thread on its way out. The re-read
                        // is load-bearing: a submitter can enqueue between this
                        // wait timing out and the lock coming back.
#pragma warning disable CA1508 // Deliberate: Monitor.Wait released the lock, so the enclosing loop's condition says nothing about the count here.
                        if (!signalled && Pending.Count == 0)
                        {
                            threads--;
                            return;
                        }
#pragma warning restore CA1508
                    }
                    run = Pending.Dequeue();
                }
                run();
            }
        }
    }

    /// <summary>
    /// Whether every expression the <em>consumer</em> evaluates is
    /// <see cref="Expression.ParallelSafe"/>. HAVING, the projection and
    /// ORDER BY are excluded deliberately: they run on the coordinator after
    /// every worker has joined, so they are unrestricted.
    /// </summary>
    private static bool ConsumerExpressionsParallelSafe(
        List<BooleanExpression> excluders,
        Expression[] groupingSet,
        List<AggregateExpression> aggregates)
    {
        foreach (var excluder in excluders)
        {
            if (!excluder.ParallelSafe)
                return false;
        }
        foreach (var grouping in groupingSet)
        {
            if (!grouping.ParallelSafe)
                return false;
        }
        foreach (var aggregate in aggregates)
        {
            if (aggregate.Operand is { } operand && !operand.ParallelSafe)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether every aggregate in the projection has an <b>exact</b> merge —
    /// one whose folded state is the state a single serial pass would have
    /// reached, value for value.
    /// <para>
    /// <c>SUM</c> / <c>AVG</c> over <c>float</c> / <c>real</c> and the
    /// statistical family accumulate in <see cref="double"/>, where a
    /// partitioned total re-associates and answers differ in the last ulp;
    /// <c>STRING_AGG</c> and the JSON aggregates concatenate in arrival order.
    /// Real's own DOP-8 plan is nondeterministic on exactly those, and the
    /// simulator chooses a determinism real doesn't offer rather than
    /// reproducing the nondeterminism.
    /// </para>
    /// </summary>
    private static bool AggregatesMergeExactly(List<AggregateExpression> aggregates, SqlType[] resultTypes)
    {
        for (var i = 0; i < aggregates.Count; i++)
        {
            var aggregate = aggregates[i];
            if (aggregate.OrderBy is not null || aggregate.Separator is not null || aggregate.KeyExpression is not null)
                return false;
            var exact = aggregate.Kind switch
            {
                AggregateKind.Count or AggregateKind.CountBig or AggregateKind.ApproxCountDistinct
                    or AggregateKind.Min or AggregateKind.Max or AggregateKind.ChecksumAgg => true,
                // The accumulator follows the result type (SumAggregator /
                // AverageAggregator's Create): long for int / bigint, decimal
                // for decimal / numeric / money, double for float — and only
                // the first two add associatively.
                AggregateKind.Sum or AggregateKind.Avg => resultTypes[i] != SqlType.Float,
                _ => false,
            };
            if (!exact)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the FROM sources are ones a worker may read from. Every source
    /// has to be a plain base table: a <see cref="FromSource.LateralPlan"/>
    /// would re-enter <see cref="Execute"/> (which writes
    /// <see cref="BatchContext"/> state) per row.
    /// <para>
    /// A <b>non-persisted computed column</b> is checked whole-table rather
    /// than per reference: <c>DecodeOrCompute</c> runs its expression through
    /// the caller's own resolver, and which columns a consumer expression can
    /// reach isn't knowable up front (a CASE arm not taken in the first rows
    /// may be taken later), so the column's own expression has to be
    /// <see cref="Expression.ParallelSafe"/> whether or not this query names
    /// it. A persisted one decodes from storage and never runs anything.
    /// </para>
    /// </summary>
    private static bool SourcesReadableInParallel(FromSource[] sources)
    {
        foreach (var source in sources)
        {
            if (source.BackingTable is null || source.LateralPlan is not null || source.IsPlaceholder)
                return false;
            foreach (var column in source.Columns)
            {
                if (column.Computed is { } computed && !column.IsPersisted && !computed.ParallelSafe)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Fans one grouping set's per-row accumulation out across worker threads.
    /// Constructed only past <see cref="ParallelRowThreshold"/> rows and only
    /// when every engagement gate holds; the coordinator then feeds it the rest
    /// of the row stream and merges the workers' group maps into its own.
    /// </summary>
    private sealed class ParallelGroupedAccumulation : IDisposable
    {
        private readonly FromSource[] sources;
        private readonly Expression[] groupingSet;
        private readonly BooleanExpression[] excluders;
        private readonly AggregateExpression[] aggregates;
        private readonly BatchContext batch;
        private readonly Func<SqlValue[], GroupState> newGroup;
        private readonly Worker[] workers;
        private readonly CancellationTokenSource cancellation = new();
        private readonly ManualResetEventSlim[] completions;

        private readonly RowBlock?[] filling;
        private int nextWorker;
        private volatile bool faulted;
        private bool joined;
        private bool disposed;

        private ParallelGroupedAccumulation(
            FromSource[] sources,
            Expression[] groupingSet,
            List<BooleanExpression> excluders,
            List<AggregateExpression> aggregates,
            BatchContext batch,
            Func<SqlValue[], GroupState> newGroup,
            int workerCount)
        {
            this.sources = sources;
            this.groupingSet = groupingSet;
            this.excluders = [.. excluders];
            this.aggregates = [.. aggregates];
            this.batch = batch;
            this.newGroup = newGroup;
            this.workers = new Worker[workerCount];
            this.completions = new ManualResetEventSlim[workerCount];
            this.filling = new RowBlock?[workerCount];
            for (var w = 0; w < workerCount; w++)
            {
                this.workers[w] = new Worker(sources.Length);
                var index = w;
                var completed = new ManualResetEventSlim(false);
                this.completions[w] = completed;
                ParallelWorkerPool.Submit(() =>
                {
                    try
                    {
                        this.Drain(index);
                    }
                    finally
                    {
                        completed.Set();
                    }
                });
            }
        }

        /// <summary>
        /// The workers' group maps, in worker order. Read after
        /// <see cref="TryComplete"/> has joined every worker.
        /// </summary>
        private Dictionary<SqlValueKey, GroupState>[] WorkerGroups
        {
            get
            {
                var maps = new Dictionary<SqlValueKey, GroupState>[this.workers.Length];
                for (var w = 0; w < this.workers.Length; w++)
                    maps[w] = this.workers[w].Groups;
                return maps;
            }
        }

        /// <summary>
        /// Builds the fan-out when every engagement gate holds, else
        /// <see langword="null"/> — in which case the caller simply keeps
        /// accumulating serially, which is the pre-existing behaviour.
        /// </summary>
        public static ParallelGroupedAccumulation? TryEngage(
            FromSource[] sources,
            Expression[] groupingSet,
            List<BooleanExpression> excluders,
            List<AggregateExpression> aggregates,
            SqlType[] aggregateResultTypes,
            BatchContext batch,
            Func<SqlValue[], GroupState> newGroup,
            Func<MultiPartName, SqlValue>? outerResolver,
            int prefixRows,
            int prefixGroups)
        {
            if (!AggregateDiagnostics.EnableParallelAccumulation
                || outerResolver is not null
                || batch.ParallelAggregateDepth != 0
                || prefixGroups * ParallelGroupDensityDivisor > prefixRows
                || !SourcesReadableInParallel(sources)
                || !AggregatesMergeExactly(aggregates, aggregateResultTypes)
                || !ConsumerExpressionsParallelSafe(excluders, groupingSet, aggregates))
            {
                AggregateDiagnostics.Sink?.Add("Aggregate:Serial");
                return null;
            }

            // Reserved last: everything above is a property of the statement,
            // while the two tests below are properties of the moment, and a
            // reservation taken before a decline would have to be handed back.
            //
            // The fan-out's share shrinks with the number of statements already
            // in flight: with idle cores it is worth eight workers, and on a
            // saturated engine it is worth none — the workers would only take
            // cores the other sessions were using, which measured as a
            // throughput *regression* on the concurrent workload drivers before
            // this test existed.
            var inFlight = Math.Max(1, batch.Connection.Simulation.StatementsInFlight);
            var share = (ParallelWorkerBudget / inFlight) - 1;
            var workerCount = ReserveParallelWorkers(Math.Min(ParallelMaxWorkers, share));
            if (workerCount < 2)
            {
                if (workerCount > 0)
                    _ = Interlocked.Add(ref outstandingParallelWorkers, -workerCount);
                AggregateDiagnostics.Sink?.Add("Aggregate:Serial(budget)");
                return null;
            }

            AggregateDiagnostics.Sink?.Add($"Aggregate:Parallel(workers={workerCount})");
            batch.ParallelAggregateDepth++;
            return new ParallelGroupedAccumulation(sources, groupingSet, excluders, aggregates, batch, newGroup, workerCount);
        }

        /// <summary>
        /// Hands one input tuple to the worker whose turn it is, copying it out
        /// of the join driver's reused array. Round-robin rather than
        /// first-free: a fixed worker per block index is what keeps each
        /// worker's rows in increasing order, which is what makes the merge's
        /// tie rules reproduce the serial answer.
        /// </summary>
        public void Offer(byte[]?[] tuple, long ordinal)
        {
            var w = this.nextWorker;
            var block = this.filling[w];
            if (block is null)
            {
                block = this.workers[w].TakeFreeBlock(this.cancellation.Token);
                block.StartOrdinal = ordinal;
                block.Count = 0;
                this.filling[w] = block;
            }

            Array.Copy(tuple, block.Rows[block.Count], tuple.Length);
            block.Count++;
            if (block.Count == ParallelBlockRows)
            {
                this.workers[w].Work.Add(block, this.cancellation.Token);
                this.filling[w] = null;
                this.nextWorker = w + 1 == this.workers.Length ? 0 : w + 1;
            }
        }

        /// <summary>
        /// Flushes the partly-filled blocks, joins every worker, and folds
        /// their group maps into <paramref name="target"/> — the coordinator's
        /// own map, holding the groups the serial prefix built.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> when a worker faulted or a merge proved
        /// inexact, in which case <paramref name="target"/> is not usable and
        /// the statement must re-run serially.
        /// </returns>
        public bool TryComplete(Dictionary<SqlValueKey, GroupState> target)
        {
            try
            {
                for (var w = 0; w < this.workers.Length; w++)
                {
                    if (this.filling[w] is { } block && block.Count > 0)
                        this.workers[w].Work.Add(block, this.cancellation.Token);
                    this.filling[w] = null;
                }
            }
            catch (OperationCanceledException)
            {
                // A worker faulted while the last blocks were being handed
                // over; the join below settles it.
            }

            this.Join();
            return !this.faulted && MergeInto(target, this.WorkerGroups, this.aggregates.Length);
        }

        /// <summary>
        /// Cancels the workers and drops everything they built — the producer's
        /// own exception path, where the statement re-runs serially and that
        /// re-run is what reports the error.
        /// </summary>
        public void Cancel()
        {
            this.faulted = true;
            this.cancellation.Cancel();
            this.Join();
        }

        /// <summary>
        /// Releases the fan-out's own resources and restores
        /// <see cref="BatchContext.ParallelAggregateDepth"/>. Joins first if
        /// nobody has: no worker may still be running when its queues go.
        /// </summary>
        public void Dispose()
        {
            if (this.disposed)
                return;
            this.disposed = true;
            if (!this.joined)
            {
                this.faulted = true;
                this.cancellation.Cancel();
                this.Join();
            }
            this.batch.ParallelAggregateDepth--;
            _ = Interlocked.Add(ref outstandingParallelWorkers, -this.workers.Length);
            this.cancellation.Dispose();
            foreach (var completed in this.completions)
                completed.Dispose();
            foreach (var worker in this.workers)
                worker.Dispose();
        }

        /// <summary>
        /// Closes every worker's inbound queue and waits for the loops to end.
        /// A worker that raised has already recorded the fault and cancelled its
        /// peers; the exception itself is dropped there, because the serial
        /// re-run is what reports the canonical error.
        /// </summary>
        private void Join()
        {
            if (this.joined)
                return;
            this.joined = true;
            foreach (var worker in this.workers)
                worker.Work.CompleteAdding();
            foreach (var completed in this.completions)
                completed.Wait();
        }

        /// <summary>
        /// Folds the worker maps into the coordinator's. A group present on
        /// both sides keeps the identity (key tuple, representative row) of
        /// whichever side's creating row came first and absorbs the other's
        /// aggregators — so the later state is always merged <em>into</em> the
        /// earlier one, which is also what gives <c>MIN</c> / <c>MAX</c> its
        /// first-of-a-tie rule. The coordinator's own groups carry ordinal
        /// zero: they came from the serial prefix, ahead of every worker row.
        /// </summary>
        private static bool MergeInto(
            Dictionary<SqlValueKey, GroupState> target,
            Dictionary<SqlValueKey, GroupState>[] workerGroups,
            int aggregateCount)
        {
            foreach (var map in workerGroups)
            {
                foreach (var pair in map)
                {
                    if (!target.TryGetValue(pair.Key, out var existing))
                    {
                        target[pair.Key] = pair.Value;
                        continue;
                    }

                    var (earlier, later) = pair.Value.FirstRowOrdinal < existing.FirstRowOrdinal
                        ? (pair.Value, existing)
                        : (existing, pair.Value);
                    for (var i = 0; i < aggregateCount; i++)
                    {
                        if (!earlier.Aggregators[i].TryMergeFrom(later.Aggregators[i]))
                            return false;
                    }
                    // The dictionary keeps its original key instance; only the
                    // state is read downstream, and the state carries its own
                    // KeyValues.
                    target[pair.Key] = earlier;
                }
            }
            return true;
        }

        /// <summary>
        /// One worker's loop: take a block, run every row in it through the
        /// same per-row pipeline the serial path runs, hand the block back.
        /// The scaffolding is hoisted exactly as the serial loop hoists it —
        /// one mutable tuple capture, one cached self-referencing resolver
        /// lambda, one <see cref="RuntimeContext"/>, one key scratch buffer —
        /// but per worker rather than per enumeration.
        /// </summary>
        private void Drain(int index)
        {
            var worker = this.workers[index];
            var memo = new SourceColumnMemo();
            var keyScratch = new SqlValue[this.groupingSet.Length];
            var currentTuple = default(byte[]?[])!;
            Func<MultiPartName, SqlValue> resolveColumn = null!;
            resolveColumn = name => ResolveAcrossTuple(this.sources, currentTuple, name, this.batch, null, memo);
            var runtime = new RuntimeContext(resolveColumn, this.batch);
            var ungrouped = this.groupingSet.Length == 0 ? this.newGroup([]) : null;
            if (ungrouped is not null)
                worker.Groups[SqlValueKey.Empty] = ungrouped;

            try
            {
                foreach (var block in worker.Work.GetConsumingEnumerable(this.cancellation.Token))
                {
                    for (var r = 0; r < block.Count; r++)
                    {
                        currentTuple = block.Rows[r];
                        var include = true;
                        foreach (var excluder in this.excluders)
                        {
                            if (excluder.Run(runtime) != true)
                            {
                                include = false;
                                break;
                            }
                        }
                        if (include)
                            this.Accumulate(worker, ungrouped, keyScratch, runtime, currentTuple, block.StartOrdinal + r);
                    }
                    worker.ReturnBlock(block);
                }
            }
            catch (OperationCanceledException)
            {
                // Another worker (or the producer) already faulted.
            }
#pragma warning disable CA1031 // Deliberate: a worker's error is a signal, not a report — the serial re-run raises the canonical one.
            catch (Exception)
            {
                this.faulted = true;
                this.cancellation.Cancel();
            }
#pragma warning restore CA1031
        }

        /// <summary>
        /// One row into its group. Mirrors the serial <c>Accumulate</c>, minus
        /// the aggregate kinds whose per-row shape needs a separator, a key or
        /// an in-parens ORDER BY — the engagement gate refuses all of those,
        /// which is what keeps this loop small enough to be obviously the same
        /// pipeline.
        /// </summary>
        private void Accumulate(
            Worker worker,
            GroupState? ungrouped,
            SqlValue[] keyScratch,
            RuntimeContext runtime,
            byte[]?[] tuple,
            long ordinal)
        {
            GroupState state;
            if (ungrouped is not null)
            {
                state = ungrouped;
            }
            else
            {
                for (var i = 0; i < this.groupingSet.Length; i++)
                    keyScratch[i] = this.groupingSet[i].Run(runtime);
                if (!worker.Groups.TryGetValue(new SqlValueKey(keyScratch), out state!))
                {
                    var keyValues = new SqlValue[this.groupingSet.Length];
                    Array.Copy(keyScratch, keyValues, keyValues.Length);
                    state = this.newGroup(keyValues);
                    worker.Groups[new SqlValueKey(keyValues)] = state;
                }
            }

            if (state.Representative is null)
            {
                // The block's row arrays are recycled, so a retained
                // representative has to be a copy — the same rule the serial
                // path follows for the join driver's reused tuple.
                var representative = new byte[]?[tuple.Length];
                Array.Copy(tuple, representative, tuple.Length);
                state.Representative = representative;
                state.FirstRowOrdinal = ordinal;
            }

            for (var i = 0; i < this.aggregates.Length; i++)
            {
                var aggregate = this.aggregates[i];
                if (aggregate.OperandUnreachable)
                    continue;
                var operand = aggregate.CountsRowsOnly ? null : aggregate.Operand;
                state.Aggregators[i].Add(operand is null ? SqlValue.Null(SqlType.Int32) : operand.Run(runtime));
            }
        }

        /// <summary>
        /// One worker's private state: its group map, its inbound block queue
        /// and the free list the producer refills from. Blocks travel
        /// producer → <see cref="Work"/> → worker → <see cref="free"/> →
        /// producer, so exactly one thread owns a block at a time and the
        /// hand-off through the collections publishes the writes.
        /// </summary>
        private sealed class Worker : IDisposable
        {
            public readonly Dictionary<SqlValueKey, GroupState> Groups = [];
            public readonly BlockingCollection<RowBlock> Work = new(ParallelBlocksPerWorker);

            private readonly BlockingCollection<RowBlock> free = new(ParallelBlocksPerWorker);

            public Worker(int sourceCount)
            {
                for (var i = 0; i < ParallelBlocksPerWorker; i++)
                    this.free.Add(new RowBlock(sourceCount));
            }

            public RowBlock TakeFreeBlock(CancellationToken cancellation) => this.free.Take(cancellation);

            // The free list is bounded at exactly the number of blocks that
            // exist, so a return never blocks.
            public void ReturnBlock(RowBlock block) => this.free.Add(block);

            public void Dispose()
            {
                this.Work.Dispose();
                this.free.Dispose();
            }
        }

        /// <summary>
        /// A run of input tuples handed to one worker, with the ordinal of its
        /// first row. The row arrays are allocated once and refilled, so the
        /// steady-state allocation of the whole fan-out is
        /// workers × blocks × rows × one small array — not one per input row.
        /// </summary>
        private sealed class RowBlock
        {
            public readonly byte[]?[][] Rows;
            public int Count;
            public long StartOrdinal;

            public RowBlock(int sourceCount)
            {
                this.Rows = new byte[]?[ParallelBlockRows][];
                for (var i = 0; i < ParallelBlockRows; i++)
                    this.Rows[i] = new byte[]?[sourceCount];
            }
        }
    }
}
