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

        // A snapshot / RCSI reader's visible version can carry a different key
        // than the live heap row, so a current-heap index could miss it — but
        // only once the table actually has version chains. With an empty version
        // store every row is implicitly committed at Xmin 0 (visible to every
        // snapshot) and the live heap IS the visible version, so the seek is
        // safe; this is the common case for read-mostly / bacpac-loaded data,
        // where declining would force a full scan on every point lookup.
        // ResolveSnapshotXidForRead is called unconditionally for its side
        // effects (pins the statement/tx snapshot xid, registers the active
        // snapshot reader) — the row-touch path relies on that bookkeeping
        // whether the read seeks or scans.
        if (batch.ResolveSnapshotXidForRead(table) is not null && !table.RowVersions.IsEmpty)
        {
            IndexSeekDiagnostics.Sink?.Add($"Scan({table.Name})");
            return sources;
        }

        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);

        // Map each indexable column of THIS source carrying a stable-value
        // equality conjunct to that value side. First writer wins per column; a
        // redundant second conjunct just stays as a residual filter.
        var equalities = new Dictionary<int, Expression>();
        foreach (var conjunct in conjuncts)
        {
            if (!conjunct.TryGetEqualityOperands(out var left, out var right))
                continue;
            _ = TryRecordColumnEquality(source, left, right, equalities, allowCorrelatedColumnValue)
                || TryRecordColumnEquality(source, right, left, equalities, allowCorrelatedColumnValue);
        }

        if (equalities.Count != 0
            && TrySeekByLongestPrefix(source, table, plan, batch, outerResolver, equalities, out var seekRows, out var width))
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
        FromSource source, Expression columnSide, Expression valueSide, Dictionary<int, Expression> equalities, bool allowCorrelatedColumnValue)
    {
        if (columnSide is not Reference columnRef)
            return false;
        var (columnSource, columnIndex) = FindSourceColumn([source], columnRef.ReferencedName);
        if (columnSource != 0)
            return false;
        var storageOrdinal = source.StorageOrdinals is { } ordinals ? ordinals[columnIndex] : columnIndex;
        return storageOrdinal >= 0
            && !source.StoredSchema[storageOrdinal].Type.IsLob
            && IsStableValueSide(valueSide, source, allowCorrelatedColumnValue)
            && equalities.TryAdd(storageOrdinal, valueSide);
    }

    // Picks the index / key whose leading key-column prefix is the longest run
    // of equality columns with usable (non-NULL, collation-compatible, cleanly
    // promoting) probe values, and seeks on that whole prefix. Probe components
    // are evaluated at most once per column via the local memo.
    private static bool TrySeekByLongestPrefix(
        FromSource source,
        HeapTable table,
        DataLockPlan plan,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        Dictionary<int, Expression> equalities,
        out IEnumerable<byte[]> seekRows,
        out int width)
    {
        seekRows = [];
        width = 0;

        var resolved = new Dictionary<int, (SqlType Common, SqlValue Probe)?>();

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
        var probe = new SqlValue[bestLen];
        for (var i = 0; i < bestLen; i++)
        {
            prefix[i] = bestKeyOrdinals is { } ko ? ko[i] : bestIndex!.KeyColumns[i].StorageOrdinal;
            var (common, value) = resolved[prefix[i]]!.Value;
            commons[i] = common;
            probe[i] = value;
        }

        var cache = seekCaches.GetValue(table.Heap, static _ => new EqualityIndexCache());
        var candidates = cache.Seek(table.Heap, source.StoredSchema, source.LobStore, prefix, commons, new SqlValueKey(probe));
        seekRows = MaterializeWithLockChecks(table, batch, plan, candidates);
        width = bestLen;
        return true;

        // Resolves (and memoizes) the probe component for one column, but only
        // for columns that carry a stable-value equality conjunct — others can't
        // anchor a seek and report as unusable, bounding the prefix there.
        (SqlType Common, SqlValue Probe)? ResolveComponent(int storageOrdinal)
        {
            if (!equalities.TryGetValue(storageOrdinal, out var valueSide))
                return null;
            if (resolved.TryGetValue(storageOrdinal, out var cached))
                return cached;
            var component = EvaluateProbeComponent(source, storageOrdinal, valueSide, batch, outerResolver);
            resolved[storageOrdinal] = component;
            return component;
        }
    }

    // Evaluates a column's equality value side to a promoted probe component, or
    // null when it can't anchor a seek: a NULL value (never equal under = ), a
    // cross-collation string compare (the domain reorders), or a promotion /
    // evaluation that throws. The value side is row-invariant
    // (IsStableValueSide), so this runs once per column per inner execution.
    private static (SqlType Common, SqlValue Probe)? EvaluateProbeComponent(
        FromSource source, int storageOrdinal, Expression valueSide, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var columnType = source.StoredSchema[storageOrdinal].Type;
        SqlValue value;
        try
        {
            value = valueSide.Run(new RuntimeContext(
                name => outerResolver is { } resolve ? resolve(name) : SqlValue.Null(SqlType.Int32),
                batch));
        }
        catch (SimulatedSqlException)
        {
            return null;
        }

        if (value.IsNull)
            return null;

        if (columnType.Category == SqlTypeCategory.String
            && value.Type.Category == SqlTypeCategory.String
            && Collation.Resolve(columnType, value.Type) is null)
        {
            return null;
        }

        try
        {
            var common = SqlType.Promote(columnType, value.Type);
            return (common, value.CoerceTo(common));
        }
        catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
        {
            return null;
        }
    }

    // Yields each candidate row's bytes after running it through the reader's
    // per-row lock / conflict check (RC probe, READPAST skip, NOLOCK pass-through
    // — exactly what the full scan applies, but only to the seeked rows).
    private static IEnumerable<byte[]> MaterializeWithLockChecks(
        HeapTable table, BatchContext batch, DataLockPlan plan, IReadOnlyList<(int Page, int Slot)> candidates)
    {
        foreach (var (page, slot) in candidates)
        {
            if (batch.TouchRowForRead(table, page, slot, plan) && table.Heap.ReadSlotBytes(page, slot) is { } bytes)
                yield return bytes;
        }
    }

    // A value side that is constant across the source's rows and free of side
    // effects, so evaluating it once for the seek and again in the residual WHERE
    // is harmless. Pure conversion wrappers (CAST / CONVERT / parens) are peeled
    // first — `id = CAST(@v AS bigint)` is as stable as `id = @v`, matching real
    // SQL Server keeping the cast sargable. The stable leaves are a literal, a
    // session variable, or a column resolving to some OTHER source (an outer /
    // correlated reference). Anything else (arithmetic, non-deterministic or
    // side-effecting functions, subqueries, or a column of THIS source) declines.
    // <paramref name="allowCorrelatedColumnValue"/> is false when narrowing the
    // leftmost source of a multi-source FROM <i>before</i> the join runs: a
    // not-yet-joined sibling column isn't resolvable then, so only literals /
    // variables / parameters qualify as the probe value.
    private static bool IsStableValueSide(Expression expression, FromSource source, bool allowCorrelatedColumnValue = true)
    {
        while (expression.PureConversionOperand is { } inner)
            expression = inner;
        return expression switch
        {
            Value => true,
            VariableReference => true,
            Reference reference => allowCorrelatedColumnValue && FindSourceColumn([source], reference.ReferencedName).SourceIndex < 0,
            _ => false,
        };
    }

    /// <summary>
    /// Lazy leading-prefix equality index for one <see cref="Heap"/>: maps a
    /// promoted key tuple to the row addresses carrying it. Keyed by the leading
    /// key-column ordinal; the entry remembers the full prefix (ordinals +
    /// promoted types) it was built for and rebuilds from a full scan when the
    /// heap's <see cref="Heap.MutationGeneration"/> moves or the requested prefix
    /// differs. Buckets hold row addresses, not row bytes, so the cache costs a
    /// few words per row rather than a copy of the table.
    /// </summary>
    private sealed class EqualityIndexCache
    {
        private static readonly List<(int Page, int Slot)> Empty = [];

        private readonly Dictionary<int, CacheEntry> byLeadOrdinal = [];

        public List<(int Page, int Slot)> Seek(
            Heap heap,
            HeapColumn[] schema,
            Heap? lobStore,
            int[] ordinals,
            SqlType[] commons,
            SqlValueKey probeKey)
        {
            var lead = ordinals[0];
            if (!this.byLeadOrdinal.TryGetValue(lead, out var entry)
                || entry.Generation != heap.MutationGeneration
                || !entry.Matches(ordinals, commons))
            {
                entry = Build(heap, schema, lobStore, ordinals, commons);
                this.byLeadOrdinal[lead] = entry;
            }

            return entry.Buckets.TryGetValue(probeKey, out var bucket) ? bucket : Empty;
        }

        private static CacheEntry Build(Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons)
        {
            var buckets = new Dictionary<SqlValueKey, List<(int Page, int Slot)>>();
            foreach (var (page, slot, bytes) in heap.EnumerateRowsWithAddress())
            {
                var components = new SqlValue[ordinals.Length];
                var anyNull = false;
                for (var i = 0; i < ordinals.Length; i++)
                {
                    var value = RowDecoder.DecodeColumn(schema, bytes, ordinals[i], lobStore);
                    if (value.IsNull)
                    {
                        anyNull = true;
                        break;
                    }

                    components[i] = value.CoerceTo(commons[i]);
                }

                // A NULL in any key component can never equal a non-NULL probe
                // (the probe components are all non-NULL by construction), so the
                // row joins no bucket.
                if (anyNull)
                    continue;

                var key = new SqlValueKey(components);
                if (!buckets.TryGetValue(key, out var bucket))
                    buckets[key] = bucket = [];
                bucket.Add((page, slot));
            }

            return new CacheEntry(heap.MutationGeneration, (int[])ordinals.Clone(), (SqlType[])commons.Clone(), buckets);
        }

        private sealed class CacheEntry(
            long generation, int[] ordinals, SqlType[] commons, Dictionary<SqlValueKey, List<(int Page, int Slot)>> buckets)
        {
            public readonly long Generation = generation;
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
        }
    }
}
