using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Multi-source FROM enumeration: cross-product / join row stream and the
/// per-row column resolver. INNER / LEFT / CROSS / CROSS APPLY / OUTER
/// APPLY stream through the same operator pipeline; RIGHT / FULL
/// materialize the right source and track a matched bitmap to emit
/// unmatched-right rows after upstream is exhausted. A derived-table
/// right side for RIGHT / FULL executes once against the enclosing-scope
/// resolver (not the joined-tuple resolver), so outer correlation works
/// but lateral correlation to the left side fails at parse time.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Resolves a column reference against a row tuple of byte[] slots
    /// (one per FROM source). A null slot indicates a NULL-filled side
    /// of an outer join — LEFT-fill at <c>tuple[s]==null</c> when
    /// <c>s</c> is the right of a LEFT JOIN, RIGHT-fill / FULL-fill at
    /// any prior slot when emitting an unmatched-right row — and the
    /// reference reads as a typed NULL.
    /// Falls through to the outer-scope resolver when no local source matches.
    /// </summary>
    private static SqlValue ResolveAcrossTuple(
        FromSource[] sources,
        byte[]?[] tuple,
        MultiPartName name,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        Func<MultiPartName, SqlValue> selfRecursive)
    {
        var (s, c) = FindSourceColumn(sources, name);
        if (s == -1)
        {
            return outerResolver is not null
                ? outerResolver(name)
                : throw SimulatedSqlException.InvalidColumnName(name);
        }

        var bytes = tuple[s];
        return bytes is null
            ? SqlValue.Null(sources[s].Columns[c].Type)
            : DecodeOrCompute(sources[s], c, bytes, batch, selfRecursive);
    }

    /// <summary>
    /// Yields the cross-product / join row stream as a sequence of
    /// <c>byte[]?[]</c> tuples, one byte[] per source (null slots
    /// indicate NULL-filled outer-join sides). The same array instance
    /// is reused across yields for efficiency — consumers must finish
    /// reading each tuple (typically by projecting / encoding the row)
    /// before advancing the enumerator.
    /// </summary>
    /// <remarks>
    /// The driver is a fold over <paramref name="joins"/>: a leftmost
    /// rowset enumerator is constructed for <c>sources[0]</c>, then each
    /// join wraps the rowset in an operator that fills its slot per
    /// upstream tuple. INNER / LEFT / CROSS / CROSS APPLY / OUTER APPLY
    /// stream one upstream tuple at a time; RIGHT / FULL materialize
    /// <c>sources[level].Rows</c> (or, for a derived-table right side,
    /// the lateral plan executed once with the enclosing-scope
    /// <paramref name="outerResolver"/>) and track a matched bitmap
    /// across the entire upstream iteration so unmatched right rows can
    /// be emitted (with NULL-filled left slots) after upstream is
    /// exhausted.
    /// </remarks>
    internal static IEnumerable<byte[]?[]> EnumerateJoinedRows(
        FromSource[] sources,
        JoinSpec[] joins,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var tuple = new byte[]?[sources.Length];

        SqlValue Resolve(MultiPartName name) =>
            ResolveAcrossTuple(sources, tuple, name, batch, outerResolver, Resolve);

        var rowset = EnumerateLeftmost(sources[0], tuple, batch, outerResolver);
        for (var level = 1; level < sources.Length; level++)
        {
            rowset = ApplyJoin(rowset, sources, joins[level - 1], tuple, level, batch, Resolve, outerResolver);
        }
        return rowset;
    }

    /// <summary>
    /// Drives <c>sources[0]</c>: its rows directly for a base / view /
    /// system table, or its <see cref="FromSource.LateralPlan"/> executed
    /// once with the enclosing-scope <paramref name="outerResolver"/>
    /// (the only correlation available at the outermost FROM level).
    /// Writes slot 0 of the shared buffer on each yield.
    /// </summary>
    private static IEnumerable<byte[]?[]> EnumerateLeftmost(
        FromSource source,
        byte[]?[] tuple,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var rows = source.LateralPlan is { } plan
            ? plan.Execute(batch, outerResolver).RowBytes
            : source.Rows;
        foreach (var row in rows)
        {
            tuple[0] = row;
            yield return tuple;
        }
        tuple[0] = null;
    }

    private static IEnumerable<byte[]?[]> ApplyJoin(
        IEnumerable<byte[]?[]> left,
        FromSource[] sources,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var right = sources[level];
        // Equi-join fast path: when the ON predicate carries at least one
        // `left.col = right.col` conjunct, hash the right source by the
        // equality keys and probe per left row — O(L + R) instead of the
        // nested loop's O(L × R). Falls back to the streaming nested-loop
        // operators for non-equi ON predicates, lateral / derived-table right
        // sides, and CROSS / APPLY (none of which can be hashed).
        var equiPlan = join.Kind is JoinKind.Inner or JoinKind.Left or JoinKind.Right or JoinKind.Full
            ? TryPlanEquiJoin(join, sources, level)
            : null;
        // INNER / LEFT equi-joins decide hash-vs-seek dynamically inside
        // EquiJoinSeekOrHash (which logs its own strategy); only the other
        // paths' strategy is fixed here.
        if (equiPlan is null)
            JoinDiagnostics.Sink?.Add($"{join.Kind}:NestedLoops");
        else if (join.Kind is JoinKind.Right or JoinKind.Full)
            JoinDiagnostics.Sink?.Add($"{join.Kind}:HashMatch(keys={equiPlan.Keys.Length},residual={equiPlan.Residual.Length})");
        return (join.Kind, equiPlan) switch
        {
            (JoinKind.Inner, { } p) => EquiJoinSeekOrHash(left, right, p, join, tuple, level, batch, resolve, emitUnmatchedLeft: false),
            (JoinKind.Left, { } p) => EquiJoinSeekOrHash(left, right, p, join, tuple, level, batch, resolve, emitUnmatchedLeft: true),
            (JoinKind.Right, { } p) => HashEquiJoin(left, right, p, tuple, level, batch, resolve, emitUnmatchedLeft: false, emitUnmatchedRight: true),
            (JoinKind.Full, { } p) => HashEquiJoin(left, right, p, tuple, level, batch, resolve, emitUnmatchedLeft: true, emitUnmatchedRight: true),
            (JoinKind.Inner or JoinKind.Cross or JoinKind.CrossApply, _) => InnerOrCross(left, right, join, tuple, level, batch, resolve),
            (JoinKind.Left or JoinKind.OuterApply, _) => LeftOrOuterApply(left, right, join, tuple, level, batch, resolve),
            (JoinKind.Right, _) => RightOuterJoin(left, right, join, tuple, level, batch, resolve, outerResolver),
            (JoinKind.Full, _) => FullOuterJoin(left, right, join, tuple, level, batch, resolve, outerResolver),
            _ => throw new NotSupportedException($"Unknown JoinKind {join.Kind}"),
        };
    }

    /// <summary>
    /// INNER JOIN (with ON), CROSS JOIN (no ON), and CROSS APPLY (no ON,
    /// lateral right): for each upstream tuple iterate right rows (or
    /// re-execute the lateral plan), check ON when present, yield matches.
    /// Upstream tuples with no match are silently dropped.
    /// </summary>
    private static IEnumerable<byte[]?[]> InnerOrCross(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve)
    {
        foreach (var _ in left)
        {
            var rows = right.LateralPlan is { } plan
                ? plan.Execute(batch, resolve).RowBytes
                : right.Rows;
            foreach (var row in rows)
            {
                tuple[level] = row;
                if (join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(resolve, batch)) == true)
                    yield return tuple;
            }
            tuple[level] = null;
        }
    }

    /// <summary>
    /// LEFT JOIN (with ON; right side may be a base table or a deferred
    /// derived table) and OUTER APPLY (no ON, lateral right): for each
    /// upstream tuple iterate right rows, check ON when present, yield
    /// matches; if no row matched, NULL-fill the level slot and yield
    /// once with the unmatched upstream tuple.
    /// </summary>
    private static IEnumerable<byte[]?[]> LeftOrOuterApply(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve)
    {
        foreach (var _ in left)
        {
            var rows = right.LateralPlan is { } plan
                ? plan.Execute(batch, resolve).RowBytes
                : right.Rows;
            var matched = false;
            foreach (var row in rows)
            {
                tuple[level] = row;
                if (join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(resolve, batch)) == true)
                {
                    matched = true;
                    yield return tuple;
                }
            }
            tuple[level] = null;
            if (!matched)
                yield return tuple;
        }
    }

    /// <summary>
    /// RIGHT [OUTER] JOIN: materializes the right source's rows up front
    /// and tracks a matched bitmap across the whole upstream iteration.
    /// For each upstream tuple, sweeps the right list, marks matches,
    /// yields matched pairs (upstream tuples without any matching right
    /// are silently dropped — that's the asymmetric flip of LEFT JOIN).
    /// After upstream is exhausted, walks the bitmap and emits each
    /// unmatched right row with all left-side slots NULL-filled. A
    /// derived-table right side (<see cref="FromSource.LateralPlan"/>)
    /// is executed once with the enclosing-scope
    /// <paramref name="outerResolver"/> — never with the joined-tuple
    /// resolver — so the inner plan can correlate to outer scope but
    /// not to the left side of the join. Real SQL Server rejects
    /// lateral correlation to the left of RIGHT/FULL with Msg 4104; the
    /// simulator's parser raises Msg 207 at parse time for the same
    /// shape because the derived-table parser doesn't see the
    /// left-source snapshot.
    /// </summary>
    private static IEnumerable<byte[]?[]> RightOuterJoin(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        List<byte[]> rightRows = right.LateralPlan is { } plan
            ? [.. plan.Execute(batch, outerResolver).RowBytes]
            : [.. right.Rows];
        var matched = new bool[rightRows.Count];

        foreach (var _ in left)
        {
            for (var i = 0; i < rightRows.Count; i++)
            {
                tuple[level] = rightRows[i];
                if (join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(resolve, batch)) == true)
                {
                    matched[i] = true;
                    yield return tuple;
                }
            }
            tuple[level] = null;
        }

        for (var j = 0; j < level; j++)
            tuple[j] = null;
        for (var i = 0; i < rightRows.Count; i++)
        {
            if (matched[i])
                continue;
            tuple[level] = rightRows[i];
            yield return tuple;
        }
        tuple[level] = null;
    }

    /// <summary>
    /// FULL [OUTER] JOIN: matched pairs emit normally; unmatched upstream
    /// tuples emit with the level slot NULL-filled; unmatched right rows
    /// (tracked across the whole upstream iteration) emit at the end with
    /// all left-side slots NULL-filled. A derived-table right side is
    /// executed once with <paramref name="outerResolver"/>; see
    /// <see cref="RightOuterJoin"/> for the parse-time / runtime split on
    /// lateral correlation.
    /// </summary>
    private static IEnumerable<byte[]?[]> FullOuterJoin(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        List<byte[]> rightRows = right.LateralPlan is { } plan
            ? [.. plan.Execute(batch, outerResolver).RowBytes]
            : [.. right.Rows];
        var matched = new bool[rightRows.Count];

        foreach (var _ in left)
        {
            var leftMatched = false;
            for (var i = 0; i < rightRows.Count; i++)
            {
                tuple[level] = rightRows[i];
                if (join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(resolve, batch)) == true)
                {
                    matched[i] = true;
                    leftMatched = true;
                    yield return tuple;
                }
            }
            tuple[level] = null;
            if (!leftMatched)
                yield return tuple;
        }

        for (var j = 0; j < level; j++)
            tuple[j] = null;
        for (var i = 0; i < rightRows.Count; i++)
        {
            if (matched[i])
                continue;
            tuple[level] = rightRows[i];
            yield return tuple;
        }
        tuple[level] = null;
    }

    /// <summary>
    /// One <c>left.col = right.col</c> equality extracted from an ON
    /// predicate. <see cref="Left"/> resolves to a source left of the join
    /// level, <see cref="Right"/> to the level's own source; both key values
    /// are coerced to <see cref="Common"/> (the operator's promotion target)
    /// before hashing so bucket equality matches the <c>=</c> operator's
    /// promote-then-compare semantics exactly.
    /// </summary>
    private sealed class EquiKey(Reference left, Reference right, SqlType common)
    {
        public readonly Reference Left = left;
        public readonly Reference Right = right;
        public readonly SqlType Common = common;
    }

    /// <summary>
    /// The hashable shape of an ON predicate: one or more equi-join
    /// <see cref="Keys"/> plus the leftover non-equality conjuncts in
    /// <see cref="Residual"/>, which are re-checked per probed candidate.
    /// </summary>
    private sealed class EquiJoinPlan(EquiKey[] keys, BooleanExpression[] residual)
    {
        public readonly EquiKey[] Keys = keys;
        public readonly BooleanExpression[] Residual = residual;
    }

    /// <summary>
    /// Splits the join's ON predicate into <c>left = right</c> equality
    /// conjuncts (hashable keys) and a residual of everything else. Returns
    /// null — signalling the nested-loop fallback — when there's no usable
    /// equality, when the right side is a lateral / derived table (no
    /// re-enumerable <see cref="FromSource.Rows"/>), or when a key pair's
    /// types can't be promoted the way the runtime <c>=</c> would (LOB,
    /// collation conflict, cross-category), so those cases keep the exact
    /// per-row error behavior of the streaming operators.
    /// </summary>
    private static EquiJoinPlan? TryPlanEquiJoin(JoinSpec join, FromSource[] sources, int level)
    {
        if (join.OnPredicate is null || sources[level].LateralPlan is not null)
        {
            return null;
        }

        var conjuncts = new List<BooleanExpression>();
        join.OnPredicate.CollectConjuncts(conjuncts);

        var keys = new List<EquiKey>();
        var residual = new List<BooleanExpression>();
        foreach (var conjunct in conjuncts)
        {
            if (TryExtractEquiKey(conjunct, sources, level, out var key))
                keys.Add(key);
            else
                residual.Add(conjunct);
        }
        return keys.Count == 0 ? null : new EquiJoinPlan([.. keys], [.. residual]);
    }

    /// <summary>
    /// Recognizes a conjunct of the form <c>leftColumn = rightColumn</c>
    /// where one bare column reference resolves to a source left of the join
    /// level and the other to the level's source. Only bare
    /// <see cref="Reference"/>s qualify — that keeps side classification a
    /// single exact <see cref="FindSourceColumn"/> lookup, so a more complex
    /// operand can never be misattributed to one side.
    /// </summary>
    private static bool TryExtractEquiKey(BooleanExpression conjunct, FromSource[] sources, int level, out EquiKey key)
    {
        key = null!;
        if (!conjunct.TryGetEqualityOperands(out var a, out var b)
            || a is not Reference refA || b is not Reference refB)
        {
            return false;
        }

        var sideA = SourceSide(refA, sources, level);
        var sideB = SourceSide(refB, sources, level);
        Reference leftRef, rightRef;
        if (sideA < 0 && sideB > 0) { leftRef = refA; rightRef = refB; }
        else if (sideA > 0 && sideB < 0) { leftRef = refB; rightRef = refA; }
        else { return false; }

        var leftType = ResolveColumnTypeAcrossSources(sources, leftRef.ReferencedName, null);
        var rightType = ResolveColumnTypeAcrossSources(sources, rightRef.ReferencedName, null);
        if (leftType.IsLob || rightType.IsLob)
        {
            return false;
        }
        if (leftType.Category == SqlTypeCategory.String && rightType.Category == SqlTypeCategory.String
            && Collation.Resolve(leftType, rightType) is null)
        {
            return false;
        }

        SqlType common;
        try
        {
            common = SqlType.Promote(leftType, rightType);
        }
        catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
        {
            return false;
        }

        key = new EquiKey(leftRef, rightRef, common);
        return true;
    }

    /// <summary>
    /// Classifies a bare column reference relative to the join level:
    /// negative = a source strictly left of the level, positive = the
    /// level's own source, zero = neither (outer scope, a not-yet-joined
    /// source, or unresolved). The sign-encoding keeps the
    /// <see cref="TryExtractEquiKey"/> test branch-light.
    /// </summary>
    private static int SourceSide(Reference reference, FromSource[] sources, int level)
    {
        var (source, _) = FindSourceColumn(sources, reference.ReferencedName);
        return source < 0 || source > level ? 0 : source < level ? -1 : 1;
    }

    private static readonly JoinSpec[] NoJoins = [];

    /// <summary>
    /// Rewrites a comma-join / explicit <c>CROSS JOIN</c> whose WHERE carries an
    /// equi-join predicate into an <c>INNER JOIN</c> with that predicate as its
    /// <c>ON</c>, so the equi-join hash / per-outer-seek machinery (which only
    /// fires for INNER / LEFT / RIGHT / FULL — a <see cref="JoinKind.Cross"/>
    /// always falls to the O(L×R) nested loop) accelerates it.
    /// <c>FROM a, b WHERE a.k = b.k</c> is the textbook equivalent of
    /// <c>a INNER JOIN b ON a.k = b.k</c>.
    /// <para>
    /// Run once at parse time (the join shape is value-independent), so the
    /// rewritten array is captured in the cached plan. Every pulled conjunct
    /// <b>stays</b> in <paramref name="excluders"/> as a residual filter — never
    /// removed — so flipping a Cross level to Inner can only drop rows the WHERE
    /// would drop anyway: the post-WHERE result is provably unchanged whatever
    /// outer joins sit elsewhere in the chain. Only Cross levels with no existing
    /// <c>ON</c> and a re-enumerable (non-lateral) right side are touched; a level
    /// that already carries an <c>ON</c> is left alone. Returns the same array
    /// when nothing rewrites — the common single-source / explicit-join case.
    /// </para>
    /// </summary>
    internal static JoinSpec[] RewriteCommaJoinsToEquiJoins(
        FromSource[] sources, JoinSpec[] joins, List<BooleanExpression> excluders)
    {
        if (sources.Length < 2 || excluders.Count == 0)
            return joins;

        List<BooleanExpression>? conjuncts = null;
        JoinSpec[]? rewritten = null;
        for (var level = 1; level < sources.Length; level++)
        {
            var join = joins[level - 1];
            // Only a Cross join with no ON is a candidate; a lateral / derived
            // right side has no re-enumerable Rows to hash or seek, so leave it
            // on the nested-loop path.
            if (join.Kind != JoinKind.Cross || join.OnPredicate is not null || sources[level].LateralPlan is not null)
                continue;

            if (conjuncts is null)
            {
                conjuncts = [];
                foreach (var excluder in excluders)
                    excluder.CollectConjuncts(conjuncts);
            }

            BooleanExpression? on = null;
            foreach (var conjunct in conjuncts)
            {
                if (TryExtractEquiKey(conjunct, sources, level, out _))
                    on = on is null ? conjunct : BooleanExpression.And(on, conjunct);
            }

            if (on is null)
                continue;
            rewritten ??= (JoinSpec[])joins.Clone();
            rewritten[level - 1] = new JoinSpec(JoinKind.Inner, on);
        }

        return rewritten ?? joins;
    }

    /// <summary>
    /// Outer-row cap for choosing the per-outer index-seek strategy. A small
    /// outer set joined to an indexed inner wins big from seeking the inner per
    /// outer row — the inner's per-<c>Heap</c> seek cache builds once and
    /// persists across outer rows and across query executions, whereas
    /// <see cref="HashEquiJoin"/> rebuilds its dictionary over the whole inner
    /// every execution. Above the cap the hash build's O(L+R) wins (per-outer
    /// seek-call overhead would dominate a large outer), so the join falls back.
    /// </summary>
    private const int SeekOuterRowCap = 128;

    /// <summary>
    /// INNER / LEFT equi-join that adaptively chooses between a per-outer index
    /// seek on the inner and the hash build. Buffers the outer up to
    /// <see cref="SeekOuterRowCap"/>; if the outer stays small <b>and</b> the
    /// inner is a base table the equality keys can seek (probed once on the
    /// first outer row — the decline conditions are value-independent), it seeks
    /// the inner per outer row and re-checks the full ON predicate as a residual
    /// filter (result-transparent). Otherwise it replays the buffered outer rows
    /// (then the remainder) into <see cref="HashEquiJoin"/>, so a large outer or
    /// an unindexed inner never regresses. RIGHT / FULL stay on the hash path
    /// (their unmatched-right tracking needs the inner materialized regardless).
    /// </summary>
    private static IEnumerable<byte[]?[]> EquiJoinSeekOrHash(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        EquiJoinPlan plan,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve,
        bool emitUnmatchedLeft)
    {
        void LogHash() => JoinDiagnostics.Sink?.Add($"{join.Kind}:HashMatch(keys={plan.Keys.Length},residual={plan.Residual.Length})");

        // Only a re-enumerable base table can be seeked per outer row.
        if (right.BackingTable is null || right.LateralPlan is not null)
        {
            LogHash();
            foreach (var t in HashEquiJoin(left, right, plan, tuple, level, batch, resolve, emitUnmatchedLeft, emitUnmatchedRight: false))
                yield return t;
            yield break;
        }

        var buffer = new List<byte[]?[]>();
        using var le = left.GetEnumerator();
        var overflow = false;
        while (le.MoveNext())
        {
            var snap = new byte[]?[level];
            Array.Copy(tuple, snap, level);
            buffer.Add(snap);
            if (buffer.Count > SeekOuterRowCap)
            {
                overflow = true;
                break;
            }
        }

        // Large outer: hash, replaying the buffered rows then the remainder.
        if (overflow)
        {
            LogHash();
            foreach (var t in HashEquiJoin(ReplayThenContinue(buffer, le, tuple, level), right, plan, tuple, level, batch, resolve, emitUnmatchedLeft, emitUnmatchedRight: false))
                yield return t;
            yield break;
        }

        if (buffer.Count == 0)
            yield break;

        // Probe seekability on the first buffered outer row. MaybeApplyIndexSeek
        // returns the same FromSource on decline, a narrowed one on seek; the
        // decline is value-independent so this one probe settles the strategy.
        Array.Copy(buffer[0], tuple, level);
        var firstSeek = MaybeApplyIndexSeek([right], NoJoins, [join.OnPredicate!], batch, resolve);
        if (ReferenceEquals(firstSeek[0], right))
        {
            LogHash();
            foreach (var t in HashEquiJoin(Replay(buffer, tuple, level), right, plan, tuple, level, batch, resolve, emitUnmatchedLeft, emitUnmatchedRight: false))
                yield return t;
            yield break;
        }

        JoinDiagnostics.Sink?.Add($"{join.Kind}:NestedLoopIndexSeek(keys={plan.Keys.Length})");
        var runtime = new RuntimeContext(resolve, batch);
        for (var i = 0; i < buffer.Count; i++)
        {
            Array.Copy(buffer[i], tuple, level);
            var seeked = i == 0 ? firstSeek : MaybeApplyIndexSeek([right], NoJoins, [join.OnPredicate!], batch, resolve);
            var matched = false;
            foreach (var row in seeked[0].Rows)
            {
                tuple[level] = row;
                if (join.OnPredicate!.Run(runtime) == true)
                {
                    matched = true;
                    yield return tuple;
                }
            }

            if (emitUnmatchedLeft && !matched)
            {
                tuple[level] = null;
                yield return tuple;
            }

            tuple[level] = null;
        }
    }

    // Replays buffered outer-slot snapshots into the shared tuple, one yield
    // each — the enumerable form HashEquiJoin's probe phase consumes.
    private static IEnumerable<byte[]?[]> Replay(List<byte[]?[]> buffer, byte[]?[] tuple, int level)
    {
        foreach (var snap in buffer)
        {
            Array.Copy(snap, tuple, level);
            yield return tuple;
        }
    }

    // Replay then drain the remaining outer enumerator (positioned just past the
    // last buffered row), so the hash path sees the full outer stream in order.
    private static IEnumerable<byte[]?[]> ReplayThenContinue(List<byte[]?[]> buffer, IEnumerator<byte[]?[]> rest, byte[]?[] tuple, int level)
    {
        foreach (var snap in buffer)
        {
            Array.Copy(snap, tuple, level);
            yield return tuple;
        }

        while (rest.MoveNext())
            yield return tuple;
    }

    /// <summary>
    /// Hash equi-join shared across INNER / LEFT / RIGHT / FULL: materialize
    /// and index the right source by the promoted equality keys, then probe
    /// once per upstream tuple. <paramref name="emitUnmatchedLeft"/> NULL-fills
    /// the right slot for an upstream row with no match (LEFT / FULL);
    /// <paramref name="emitUnmatchedRight"/> tracks a matched bitmap and, after
    /// upstream is exhausted, emits each unmatched right row with the left
    /// slots NULL-filled (RIGHT / FULL). NULL-keyed rows never match (NULL =
    /// NULL is UNKNOWN) but are retained for the unmatched-right tail.
    /// </summary>
    private static IEnumerable<byte[]?[]> HashEquiJoin(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        EquiJoinPlan plan,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve,
        bool emitUnmatchedLeft,
        bool emitUnmatchedRight)
    {
        var runtime = new RuntimeContext(resolve, batch);

        var rightRows = new List<byte[]>();
        var index = new Dictionary<SqlValueKey, List<int>>();
        foreach (var row in right.Rows)
        {
            tuple[level] = row;
            var ordinal = rightRows.Count;
            rightRows.Add(row);
            if (TryComputeKey(plan.Keys, runtime, rightSide: true, out var buildKey))
            {
                if (!index.TryGetValue(buildKey, out var bucket))
                    index[buildKey] = bucket = [];
                bucket.Add(ordinal);
            }
        }
        tuple[level] = null;

        var matchedRight = new bool[rightRows.Count];

        foreach (var _ in left)
        {
            var matchedLeft = false;
            if (TryComputeKey(plan.Keys, runtime, rightSide: false, out var probeKey)
                && index.TryGetValue(probeKey, out var bucket))
            {
                foreach (var ordinal in bucket)
                {
                    tuple[level] = rightRows[ordinal];
                    if (ResidualMatches(plan.Residual, runtime))
                    {
                        matchedLeft = true;
                        matchedRight[ordinal] = true;
                        yield return tuple;
                    }
                }
                tuple[level] = null;
            }
            if (emitUnmatchedLeft && !matchedLeft)
                yield return tuple;
        }

        if (!emitUnmatchedRight)
            yield break;

        for (var j = 0; j < level; j++)
            tuple[j] = null;
        for (var i = 0; i < rightRows.Count; i++)
        {
            if (matchedRight[i])
                continue;
            tuple[level] = rightRows[i];
            yield return tuple;
        }
        tuple[level] = null;
    }

    /// <summary>
    /// Computes the composite bucket key for one side of the join, coercing
    /// each key value to its <see cref="EquiKey.Common"/> promotion type.
    /// Returns false (no bucket) the moment any key value is NULL.
    /// </summary>
    private static bool TryComputeKey(EquiKey[] keys, RuntimeContext runtime, bool rightSide, out SqlValueKey key)
    {
        var values = new SqlValue[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            var raw = (rightSide ? keys[i].Right : keys[i].Left).Run(runtime);
            if (raw.IsNull)
            {
                key = default;
                return false;
            }
            values[i] = raw.CoerceTo(keys[i].Common);
        }
        key = new SqlValueKey(values);
        return true;
    }

    /// <summary>
    /// True when every residual (non-equi) conjunct evaluates to <c>true</c>
    /// for the current tuple — UNKNOWN and false both fail, matching the
    /// <c>== true</c> gate the streaming ON-predicate path applies.
    /// </summary>
    private static bool ResidualMatches(BooleanExpression[] residual, RuntimeContext runtime)
    {
        foreach (var conjunct in residual)
        {
            if (conjunct.Run(runtime) != true)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves a single column reference at <paramref name="columnIndex"/>
    /// in <paramref name="source"/> for the row at <paramref name="bytes"/>.
    /// Stored columns (regular plus persisted-computed) decode directly via
    /// <see cref="RowDecoder.DecodeColumn(ReadOnlySpan{HeapColumn}, ReadOnlySpan{byte}, int, Heap?)"/>
    /// at their storage ordinal. Non-persisted computed columns evaluate
    /// their expression through <paramref name="resolveByName"/> — the
    /// recursive references inside the expression bind back through the same
    /// caller's resolver, but are guaranteed by Msg 1759 to land only on
    /// stored columns.
    /// </summary>
    private static SqlValue DecodeOrCompute(
        FromSource source,
        int columnIndex,
        byte[] bytes,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolveByName) =>
        source.StorageOrdinals is null
            ? RowDecoder.DecodeColumn(source.StoredSchema, bytes, columnIndex, source.LobStore)
            : source.Columns[columnIndex].Computed is { } computedExpr && !source.Columns[columnIndex].IsPersisted
                ? computedExpr.Run(new RuntimeContext(resolveByName, batch))
                : RowDecoder.DecodeColumn(source.StoredSchema, bytes, source.StorageOrdinals[columnIndex], source.LobStore);
}
