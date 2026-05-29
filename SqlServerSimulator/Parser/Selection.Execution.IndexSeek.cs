using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

internal sealed partial class Selection
{
    // Per-Heap equality-seek cache, attached without modifying the storage
    // layer. Evicted when the Heap is garbage-collected (and rebuilt whenever
    // its mutation generation moves — see Heap.MutationGeneration). ALTER TABLE
    // replaces a table's Heap wholesale, so the new instance starts cache-free.
    private static readonly ConditionalWeakTable<Heap, EqualityIndexCache> seekCaches = [];

    /// <summary>
    /// Narrows a single-base-table scan to an index seek when WHERE carries
    /// top-level conjuncts of the shape <c>indexedColumn = &lt;stable value&gt;</c>
    /// — a literal, a variable, or an outer/correlated column reference. This
    /// is what collapses a correlated <c>EXISTS</c> / <c>IN</c> / scalar
    /// subquery (and an unindexed-inner <c>APPLY</c>) from O(outer × inner) to
    /// one cache build plus O(outer) seeks: the inner re-executes per outer row
    /// but the per-table cache persists across those calls.
    /// <para>
    /// When several equality conjuncts line up with the <b>leading prefix</b> of
    /// one index's key columns (<c>a = x AND b = y</c> against an index on
    /// <c>(a, b, …)</c>), the seek keys on the whole matched prefix — so a
    /// non-selective leading column (a flag, a low-cardinality FK) no longer
    /// drags the whole bucket through the residual filter. The longest usable
    /// prefix across all keys / indexes wins.
    /// </para>
    /// <para>
    /// Returns the same array when no seek applies. Every matched conjunct is
    /// <b>kept</b> in <paramref name="excluders"/> as a residual filter, so the
    /// seek can only narrow the row source — never change results. The value
    /// side is restricted to side-effect-free, row-invariant shapes precisely
    /// so evaluating it once here and again in the residual WHERE is harmless.
    /// </para>
    /// </summary>
    private static FromSource[] MaybeApplyIndexSeek(
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        bool allowCorrelatedColumnValue = true)
    {
        if (sources.Length != 1 || joins.Length != 0 || excluders.Count == 0)
            return sources;
        var source = sources[0];
        if (source.BackingTable is not { } table || source.LateralPlan is not null)
            return sources;

        // The seek narrows the row source, then routes each candidate through
        // the SAME per-row lock / conflict pipeline the full scan uses — so it
        // touches (and locks) only the seeked rows, matching a real index seek.
        // tx-scoped row locks (REPEATABLE READ / SERIALIZABLE / UPDLOCK …) keep
        // the whole-table scan, which deliberately locks every row it reads to
        // end of transaction.
        if (source.HeapPlan is not { } plan || plan.RowTxScoped)
        {
            IndexSeekDiagnostics.Sink?.Add($"Scan({table.Name})");
            return sources;
        }

        // A snapshot / RCSI reader sees the version visible at its snapshot, not
        // necessarily the live heap row. ResolveSnapshotXidForRead returns that
        // snapshot xid (and pins the statement / tx snapshot as a side effect the
        // row-touch path relies on whether the read seeks or scans). When it's
        // non-null the seek still runs, but materializes each candidate through
        // the version store and additionally sweeps the rows carrying a version
        // chain — those are the only rows whose snapshot-visible key can differ
        // from their live key, so a live-key-only seek could miss them. With an
        // empty version store the sweep is empty and every candidate resolves to
        // its live bytes. See MaterializeSnapshotCandidates.
        var snapshotXid = batch.ResolveSnapshotXidForRead(table);

        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);

        // Map each indexable column of THIS source carrying a stable-value
        // equality conjunct (or IN-list / OR-of-equalities on the same column)
        // to its value side(s). First writer wins per column; a redundant
        // later conjunct just stays as a residual filter.
        var equalities = new Dictionary<int, Expression[]>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetEqualityOperands(out var left, out var right))
            {
                _ = TryRecordColumnEquality(source, left, right, equalities, allowCorrelatedColumnValue)
                    || TryRecordColumnEquality(source, right, left, equalities, allowCorrelatedColumnValue);
                continue;
            }
            if (conjunct.TryGetEqualityFamily(out var family))
                _ = TryRecordEqualityFamily(source, family, equalities, allowCorrelatedColumnValue);
        }

        if (equalities.Count != 0
            && TrySeekByLongestPrefix(source, table, plan, batch, snapshotXid, outerResolver, equalities, out var seekRows, out var width))
        {
            IndexSeekDiagnostics.Sink?.Add($"Seek({table.Name})");
            IndexSeekDiagnostics.Sink?.Add($"SeekWidth({table.Name},{width})");
            return
            [
                new FromSource(
                    source.Qualifier, source.ColumnNames, source.Columns, source.StoredSchema,
                    source.StorageOrdinals, source.LobStore, seekRows, source.LateralPlan,
                    source.BackingTable, source.BackingView),
            ];
        }

        IndexSeekDiagnostics.Sink?.Add($"Scan({table.Name})");
        return sources;
    }

    /// <summary>
    /// Pushes single-source WHERE equality predicates (<c>leftmostCol = literal /
    /// variable</c>) down onto the leftmost FROM source of a multi-source query,
    /// seeking it before the join runs. The leftmost source is always preserved
    /// (never the NULL-supplied side of an outer join), so narrowing it can't
    /// change join semantics, and the conjuncts stay in the residual WHERE.
    /// Probe values are restricted to non-column constants/variables — a
    /// not-yet-joined sibling column isn't resolvable pre-join. Shrinking the
    /// driving rowset is what lets <see cref="EquiJoinSeekOrHash"/> seek the
    /// inner per outer row for the common filter-then-join shape.
    /// </summary>
    private static FromSource[] NarrowLeftmostJoinSource(
        FromSource[] sources, List<BooleanExpression> excluders, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (sources.Length < 2 || excluders.Count == 0)
            return sources;
        var seeked = MaybeApplyIndexSeek([sources[0]], NoJoins, excluders, batch, outerResolver, allowCorrelatedColumnValue: false);
        if (ReferenceEquals(seeked[0], sources[0]))
            return sources;
        var result = (FromSource[])sources.Clone();
        result[0] = seeked[0];
        return result;
    }

    // Records `column = stableValue` for an indexable, non-LOB column of THIS
    // source. No evaluation happens here — only the value-side expression is
    // captured; it's run lazily (and once) when a prefix actually selects it.
    private static bool TryRecordColumnEquality(
        FromSource source, Expression columnSide, Expression valueSide, Dictionary<int, Expression[]> equalities, bool allowCorrelatedColumnValue)
        => TryIdentifyIndexableColumn(source, columnSide, out var storageOrdinal)
            && IsStableValueSide(valueSide, source, allowCorrelatedColumnValue)
            && equalities.TryAdd(storageOrdinal, [valueSide]);

    // Records `column IN (v1, v2, ...)` (or the equivalent OR-of-equalities)
    // for one indexable, non-LOB column of THIS source — every pair in the
    // family must put the column on one side and a stable value on the other,
    // and they must all agree on the column. Order within the family is
    // preserved; duplicates aren't deduplicated (a row matches at most one
    // probe per column anyway, so duplicate probes just waste a hash lookup).
    private static bool TryRecordEqualityFamily(
        FromSource source,
        List<(Expression Left, Expression Right)> family,
        Dictionary<int, Expression[]> equalities,
        bool allowCorrelatedColumnValue)
    {
        if (family.Count == 0)
            return false;

        int? targetStorageOrdinal = null;
        var values = new Expression[family.Count];
        for (var i = 0; i < family.Count; i++)
        {
            var (left, right) = family[i];
            if (!TryExtractColumnAndValue(source, left, right, allowCorrelatedColumnValue, out var ord, out var value)
                && !TryExtractColumnAndValue(source, right, left, allowCorrelatedColumnValue, out ord, out value))
            {
                return false;
            }
            if (targetStorageOrdinal is { } existing && existing != ord)
                return false;
            targetStorageOrdinal = ord;
            values[i] = value;
        }

        return equalities.TryAdd(targetStorageOrdinal!.Value, values);
    }

    private static bool TryExtractColumnAndValue(
        FromSource source,
        Expression columnSide,
        Expression valueSide,
        bool allowCorrelatedColumnValue,
        out int storageOrdinal,
        [NotNullWhen(true)] out Expression? value)
    {
        if (TryIdentifyIndexableColumn(source, columnSide, out storageOrdinal)
            && IsStableValueSide(valueSide, source, allowCorrelatedColumnValue))
        {
            value = valueSide;
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryIdentifyIndexableColumn(FromSource source, Expression candidate, out int storageOrdinal)
    {
        storageOrdinal = -1;
        if (candidate is not Reference columnRef)
            return false;
        var (columnSource, columnIndex) = FindSourceColumn([source], columnRef.ReferencedName);
        if (columnSource != 0)
            return false;
        var ord = source.StorageOrdinals is { } ordinals ? ordinals[columnIndex] : columnIndex;
        if (ord < 0 || source.StoredSchema[ord].Type.IsLob)
            return false;
        storageOrdinal = ord;
        return true;
    }

    // Picks the index / key whose leading key-column prefix is the longest run
    // of equality columns with usable (non-NULL, collation-compatible, cleanly
    // promoting) probe values, and seeks on that whole prefix. Probe components
    // are evaluated at most once per column via the local memo. When a column
    // is bound to an IN-list / OR-equality family, every probe expands across
    // the cartesian product of selected columns — `a IN (1,2) AND b = 3` fires
    // two probes against the (a,b) composite cache, `a IN (1,2) AND b IN (3,4)`
    // fires four. A single per-column NULL is skipped silently (never equal
    // under <c>=</c>); if EVERY probe in a column collapses to NULL the column
    // declines and the prefix stops there.
    private static bool TrySeekByLongestPrefix(
        FromSource source,
        HeapTable table,
        DataLockPlan plan,
        BatchContext batch,
        long? snapshotXid,
        Func<MultiPartName, SqlValue>? outerResolver,
        Dictionary<int, Expression[]> equalities,
        out IEnumerable<byte[]> seekRows,
        out int width)
    {
        seekRows = [];
        width = 0;

        var resolved = new Dictionary<int, (SqlType Common, SqlValue[] Probes)?>();

        var bestLen = 0;
        int[]? bestKeyOrdinals = null;
        Storage.Index? bestIndex = null;

        foreach (var key in table.KeyConstraints)
        {
            var ordinals = key.StorageOrdinals;
            var len = 0;
            while (len < ordinals.Length && ResolveComponent(ordinals[len]) is not null)
                len++;
            if (len > bestLen)
            {
                bestLen = len;
                bestKeyOrdinals = ordinals;
                bestIndex = null;
            }
        }

        foreach (var index in table.Indexes)
        {
            var keyColumns = index.KeyColumns;
            var len = 0;
            while (len < keyColumns.Length && ResolveComponent(keyColumns[len].StorageOrdinal) is not null)
                len++;
            if (len > bestLen)
            {
                bestLen = len;
                bestIndex = index;
                bestKeyOrdinals = null;
            }
        }

        if (bestLen == 0)
            return false;

        var prefix = new int[bestLen];
        var commons = new SqlType[bestLen];
        var probesPerColumn = new SqlValue[bestLen][];
        for (var i = 0; i < bestLen; i++)
        {
            prefix[i] = bestKeyOrdinals is { } ko ? ko[i] : bestIndex!.KeyColumns[i].StorageOrdinal;
            var (common, probes) = resolved[prefix[i]]!.Value;
            commons[i] = common;
            probesPerColumn[i] = probes;
        }

        var cache = seekCaches.GetValue(table.Heap, static _ => new EqualityIndexCache());
        var candidates = new List<(int Page, int Slot)>();
        foreach (var tuple in CartesianProduct(probesPerColumn))
        {
            var bucket = cache.Seek(table.Heap, source.StoredSchema, source.LobStore, prefix, commons, new SqlValueKey(tuple));
            if (bucket.Count != 0)
                candidates.AddRange(bucket);
        }

        seekRows = snapshotXid is { } sx
            ? MaterializeSnapshotCandidates(table, batch, sx, candidates)
            : MaterializeWithLockChecks(table, batch, plan, candidates);
        width = bestLen;
        return true;

        // Resolves (and memoizes) the probe components for one column, but only
        // for columns that carry a stable-value equality conjunct (or IN-list /
        // OR family). Others can't anchor a seek and report as unusable,
        // bounding the prefix there.
        (SqlType Common, SqlValue[] Probes)? ResolveComponent(int storageOrdinal)
        {
            if (!equalities.TryGetValue(storageOrdinal, out var valueSides))
                return null;
            if (resolved.TryGetValue(storageOrdinal, out var cached))
                return cached;
            var component = EvaluateProbeComponent(source, storageOrdinal, valueSides, batch, outerResolver);
            resolved[storageOrdinal] = component;
            return component;
        }
    }

    // Yields every cartesian-product tuple across the per-column probe arrays.
    // For an N-column prefix where the i-th column has c_i probe values, the
    // total tuple count is the product of the c_i; for the common single-value
    // case (all c_i == 1) this yields exactly one tuple.
    private static IEnumerable<SqlValue[]> CartesianProduct(SqlValue[][] perColumn)
    {
        var indices = new int[perColumn.Length];
        while (true)
        {
            var tuple = new SqlValue[perColumn.Length];
            for (var i = 0; i < perColumn.Length; i++)
                tuple[i] = perColumn[i][indices[i]];
            yield return tuple;

            var carry = perColumn.Length - 1;
            while (carry >= 0)
            {
                indices[carry]++;
                if (indices[carry] < perColumn[carry].Length)
                    break;
                indices[carry] = 0;
                carry--;
            }
            if (carry < 0)
                yield break;
        }
    }

    // Evaluates a column's equality value side(s) into promoted probe components,
    // or null when none can anchor a seek: NULL values are skipped (never equal
    // under = ) and an empty result drops the column; cross-collation string
    // compares or promotion failures also drop the individual probe. A single
    // unified promoted type is chosen by promoting pairwise across all surviving
    // probes — pinning the bucket key to one type. The value side is
    // row-invariant (IsStableValueSide), so this runs once per column per
    // inner execution. Duplicates aren't deduplicated; the cache lookup is a
    // hash hit and duplicate keys just probe the same bucket.
    private static (SqlType Common, SqlValue[] Probes)? EvaluateProbeComponent(
        FromSource source, int storageOrdinal, Expression[] valueSides, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var columnType = source.StoredSchema[storageOrdinal].Type;
        var raw = new List<SqlValue>(valueSides.Length);
        SqlType? common = null;

        foreach (var valueSide in valueSides)
        {
            SqlValue value;
            try
            {
                value = valueSide.Run(new RuntimeContext(
                    name => outerResolver is { } resolve ? resolve(name) : SqlValue.Null(SqlType.Int32),
                    batch));
            }
            catch (SimulatedSqlException)
            {
                continue;
            }

            if (value.IsNull)
                continue;

            if (columnType.Category == SqlTypeCategory.String
                && value.Type.Category == SqlTypeCategory.String
                && Collation.Resolve(columnType, value.Type) is null)
            {
                continue;
            }

            SqlType promoted;
            try
            {
                promoted = SqlType.Promote(columnType, value.Type);
            }
            catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
            {
                continue;
            }

            try
            {
                common = common is null ? promoted : SqlType.Promote(common, promoted);
            }
            catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
            {
                return null;
            }

            raw.Add(value);
        }

        if (common is null || raw.Count == 0)
            return null;

        var probes = new SqlValue[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            try
            {
                probes[i] = raw[i].CoerceTo(common);
            }
            catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
            {
                return null;
            }
        }

        return (common, probes);
    }

    // Yields each candidate row's bytes after running it through the reader's
    // per-row lock / conflict check (RC probe, READPAST skip, NOLOCK pass-through
    // — exactly what the full scan applies, but only to the seeked rows).
    private static IEnumerable<byte[]> MaterializeWithLockChecks(
        HeapTable table, BatchContext batch, DataLockPlan plan, IReadOnlyList<(int Page, int Slot)> candidates)
    {
        // Dedup + tombstone-skip mirror the full scan's EnumerateRowsWithAddress
        // (which skips tombstoned / forward-target slots and yields each row once)
        // and neutralize the incrementally-maintained cache's only imprecision: a
        // not-yet-applied or mis-keyed Delete can leave a tombstoned address in a
        // bucket, and a double-applied Insert can list one twice.
        var seen = new HashSet<(int, int)>();
        foreach (var (page, slot) in candidates)
        {
            if (!seen.Add((page, slot)) || table.Heap.IsSlotTombstoned(page, slot))
                continue;
            if (batch.TouchRowForRead(table, page, slot, plan) && table.Heap.ReadSlotBytes(page, slot) is { } bytes)
                yield return bytes;
        }
    }

    // Snapshot / RCSI candidate materialization. Mirrors the snapshot branch of
    // BatchContext.WrapWithRowConflictChecks, but over the seeked candidate set
    // rather than the whole heap. Two sources, deduplicated by slot:
    //   1. Bucket candidates — live rows whose CURRENT key matched the probe.
    //      Each resolves to the version visible at the snapshot (live bytes when
    //      the row carries no chain), or drops out when not visible.
    //   2. Version-chain sweep — every slot in RowVersions. These are the only
    //      rows whose snapshot-visible key can differ from their live key (so a
    //      live-key-only bucket lookup could miss them) plus tombstoned slots
    //      whose pre-delete version a pre-delete snapshot still sees. Bounded by
    //      |RowVersions|, which a read-mostly RCSI workload keeps small (the GC
    //      trims versions no open snapshot needs). The whole-table scan this
    //      replaces already walks RowVersions in its own second pass, so the
    //      seek is never more expensive than the scan it supplants.
    // The matched equality conjuncts stay in the residual WHERE, so any candidate
    // whose resolved version doesn't actually match the probe is filtered there.
    private static IEnumerable<byte[]> MaterializeSnapshotCandidates(
        HeapTable table, BatchContext batch, long snapshotXid, IReadOnlyList<(int Page, int Slot)> bucketCandidates)
    {
        var tx = batch.Connection.CurrentTransaction;
        var seen = new HashSet<(int, int)>();

        foreach (var (page, slot) in bucketCandidates)
        {
            if (!seen.Add((page, slot)))
                continue;
            if (table.Heap.ReadSlotBytes(page, slot) is { } live
                && Storage.VersionStore.ResolveVisibleVersion(table, (page, slot), live, snapshotXid, tx) is { } resolved)
            {
                yield return resolved;
            }
        }

        foreach (var kv in table.RowVersions)
        {
            var page = kv.Key.PageIndex;
            var slot = kv.Key.SlotIndex;
            if (!seen.Add((page, slot)))
                continue;

            var resolved = table.Heap.IsSlotTombstoned(page, slot)
                ? Storage.VersionStore.ResolveTombstonedSlotForSnapshot(kv.Value, snapshotXid, tx)
                : table.Heap.ReadSlotBytes(page, slot) is { } live
                    ? Storage.VersionStore.ResolveVisibleVersion(table, (page, slot), live, snapshotXid, tx)
                    : null;
            if (resolved is { } bytes)
                yield return bytes;
        }
    }

    // A value side that is constant across the source's rows and free of side
    // effects, so evaluating it once for the seek and again in the residual WHERE
    // is harmless. Pure conversion wrappers (CAST / CONVERT / parens) are peeled
    // first — `id = CAST(@v AS bigint)` is as stable as `id = @v`, matching real
    // SQL Server keeping the cast sargable. The stable leaves are a literal, a
    // session variable, or a column resolving to some OTHER source (an outer /
    // correlated reference). An arithmetic node (which is how the parser
    // represents a negative literal: `-1` is `0 - 1`) is stable when both its
    // operands are — a deterministic operator over row-invariant operands is
    // itself row-invariant, so `id = -1` / `id = @v + 1` seek too, and the
    // recursion still excludes a column of THIS source or a non-deterministic /
    // side-effecting function / subquery leaf (those decline).
    // <paramref name="allowCorrelatedColumnValue"/> is false when narrowing the
    // leftmost source of a multi-source FROM <i>before</i> the join runs: a
    // not-yet-joined sibling column isn't resolvable then, so only literals /
    // variables / parameters (and arithmetic over them) qualify as the probe value.
    private static bool IsStableValueSide(Expression expression, FromSource source, bool allowCorrelatedColumnValue = true)
    {
        while (expression.PureConversionOperand is { } inner)
            expression = inner;
        return expression switch
        {
            Value => true,
            VariableReference => true,
            Reference reference => allowCorrelatedColumnValue && FindSourceColumn([source], reference.ReferencedName).SourceIndex < 0,
            TwoSidedExpression arithmetic => arithmetic.BothOperandsMatch(operand => IsStableValueSide(operand, source, allowCorrelatedColumnValue)),
            _ => false,
        };
    }

    /// <summary>
    /// Incrementally-maintained leading-prefix equality index for one
    /// <see cref="Heap"/>: maps a promoted key tuple to the row addresses
    /// carrying it. Keyed by the leading key-column ordinal; the entry remembers
    /// the full prefix (ordinals + promoted types) it was built for. The first
    /// seek builds an entry from a full scan and activates the heap's seek
    /// journal (<see cref="Heap.ActivateSeekJournal"/>); thereafter, when the
    /// heap's <see cref="Heap.MutationGeneration"/> has moved, the entry applies
    /// the journal delta (<see cref="Heap.SnapshotSeekJournalSince"/>) rather than
    /// rebuilding — the "no warm-up" path. A full rebuild happens only when the
    /// requested prefix differs, the journal can't cover the delta (a large bulk
    /// mutation trimmed it, or a rollback / TRUNCATE invalidated it), or the heap
    /// was never journaled.
    /// <para>
    /// Buckets hold row addresses, not row bytes, so the cache costs a few words
    /// per row rather than a copy of the table. The seek keeps every matched
    /// equality conjunct in the residual WHERE, so a stale bucket membership is
    /// only ever a harmless false-positive (filtered there); the maintenance only
    /// has to avoid dropping a live candidate — inserts and update-new-keys
    /// recompute from live row bytes, so those adds are always present.
    /// </para>
    /// </summary>
    private sealed class EqualityIndexCache
    {
        private static readonly List<(int Page, int Slot)> Empty = [];

        private readonly Dictionary<int, CacheEntry> byLeadOrdinal = [];

        // Build / replay / read are serialized: the per-Heap cache is shared
        // across connections, so two readers can seek the same heap at once, and
        // a returned bucket is copied out under this lock by the caller's
        // AddRange before any concurrent mutation can patch it.
        private readonly Lock gate = new();

        public List<(int Page, int Slot)> Seek(
            Heap heap,
            HeapColumn[] schema,
            Heap? lobStore,
            int[] ordinals,
            SqlType[] commons,
            SqlValueKey probeKey)
        {
            lock (this.gate)
            {
                var lead = ordinals[0];
                if (this.byLeadOrdinal.TryGetValue(lead, out var entry) && entry.Matches(ordinals, commons))
                {
                    if (entry.Generation != heap.MutationGeneration)
                    {
                        var events = heap.SnapshotSeekJournalSince(entry.Generation, out var currentGen);
                        if (events is not null)
                        {
                            IndexSeekDiagnostics.Sink?.Add("CacheReplay");
                            entry.Apply(events, schema, lobStore, currentGen);
                        }
                        else
                        {
                            entry = this.Rebuild(heap, schema, lobStore, ordinals, commons, lead);
                        }
                    }
                }
                else
                {
                    entry = this.Rebuild(heap, schema, lobStore, ordinals, commons, lead);
                }

                return entry.Buckets.TryGetValue(probeKey, out var bucket) ? bucket : Empty;
            }
        }

        private CacheEntry Rebuild(Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons, int lead)
        {
            // Activate journaling and capture the build generation BEFORE scanning,
            // so any mutation that lands during the scan is journaled at a later
            // generation and replayed on the next seek — never silently missed.
            // (A write the scan happened to also see just replays as a harmless
            // re-add; the residual WHERE and the materializer's dedup absorb it.)
            IndexSeekDiagnostics.Sink?.Add("CacheBuild");
            var buildGen = heap.ActivateSeekJournal();
            var buckets = new Dictionary<SqlValueKey, List<(int Page, int Slot)>>();
            foreach (var (page, slot, bytes) in heap.EnumerateRowsWithAddress())
            {
                if (TryComputeKey(bytes, ordinals, commons, schema, lobStore, out var key))
                    AddRid(buckets, key, (page, slot));
            }

            var entry = new CacheEntry(buildGen, (int[])ordinals.Clone(), (SqlType[])commons.Clone(), buckets);
            this.byLeadOrdinal[lead] = entry;
            return entry;
        }

        // Decodes this entry's key tuple from a row image, coercing each component
        // to the entry's promoted type. Returns false when any component is NULL —
        // a NULL key can never equal a (non-NULL by construction) probe, so the
        // row joins no bucket.
        private static bool TryComputeKey(
            ReadOnlySpan<byte> image, int[] ordinals, SqlType[] commons, HeapColumn[] schema, Heap? lobStore, out SqlValueKey key)
        {
            var components = new SqlValue[ordinals.Length];
            for (var i = 0; i < ordinals.Length; i++)
            {
                var value = RowDecoder.DecodeColumn(schema, image, ordinals[i], lobStore);
                if (value.IsNull)
                {
                    key = default;
                    return false;
                }

                components[i] = value.CoerceTo(commons[i]);
            }

            key = new SqlValueKey(components);
            return true;
        }

        private static void AddRid(Dictionary<SqlValueKey, List<(int Page, int Slot)>> buckets, SqlValueKey key, (int Page, int Slot) rid)
        {
            if (!buckets.TryGetValue(key, out var bucket))
                buckets[key] = bucket = [];
            bucket.Add(rid);
        }

        private sealed class CacheEntry(
            long generation, int[] ordinals, SqlType[] commons, Dictionary<SqlValueKey, List<(int Page, int Slot)>> buckets)
        {
            public long Generation = generation;
            public readonly Dictionary<SqlValueKey, List<(int Page, int Slot)>> Buckets = buckets;
            private readonly int[] ordinals = ordinals;
            private readonly SqlType[] commons = commons;

            // Reuse is sound only when the cached prefix is the same column
            // sequence promoted to the same types as the incoming probe.
            public bool Matches(int[] requestedOrdinals, SqlType[] requestedCommons)
            {
                if (this.ordinals.Length != requestedOrdinals.Length)
                    return false;
                for (var i = 0; i < this.ordinals.Length; i++)
                {
                    if (this.ordinals[i] != requestedOrdinals[i] || !ReferenceEquals(this.commons[i], requestedCommons[i]))
                        return false;
                }

                return true;
            }

            // Applies the journal delta to this entry's buckets. Insert adds the
            // new key's address; Delete removes the old key's; Update does both.
            // A key recomputed from a superseded (Delete / Update-old) image whose
            // off-row chain was already reclaimed may be wrong, which can only
            // leave a stale address in a bucket — a false-positive the residual
            // WHERE and the materializer's tombstone-skip discard. The add side
            // (Insert / Update-new) decodes a live image, so it never goes wrong.
            public void Apply(Heap.SeekJournalEvent[] events, HeapColumn[] schema, Heap? lobStore, long currentGen)
            {
                foreach (var e in events)
                {
                    switch (e.Kind)
                    {
                        case Heap.SeekJournalKind.Insert:
                            if (TryComputeKey(e.NewImage, this.ordinals, this.commons, schema, lobStore, out var insertKey))
                                AddRid(this.Buckets, insertKey, (e.Page, e.Slot));
                            break;
                        case Heap.SeekJournalKind.Delete:
                            if (TryComputeKey(e.OldImage, this.ordinals, this.commons, schema, lobStore, out var deleteKey))
                                this.RemoveRid(deleteKey, (e.Page, e.Slot));
                            break;
                        case Heap.SeekJournalKind.Update:
                            if (TryComputeKey(e.OldImage, this.ordinals, this.commons, schema, lobStore, out var oldKey))
                                this.RemoveRid(oldKey, (e.Page, e.Slot));
                            if (TryComputeKey(e.NewImage, this.ordinals, this.commons, schema, lobStore, out var newKey))
                                AddRid(this.Buckets, newKey, (e.Page, e.Slot));
                            break;
                    }
                }

                this.Generation = currentGen;
            }

            private void RemoveRid(SqlValueKey key, (int Page, int Slot) rid)
            {
                if (this.Buckets.TryGetValue(key, out var bucket))
                {
                    _ = bucket.Remove(rid);
                    if (bucket.Count == 0)
                        _ = this.Buckets.Remove(key);
                }
            }
        }
    }
}
