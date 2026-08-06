using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Per-batch ceiling on cascading FK actions. Real SQL Server enforces a
    /// limit of 32; the simulator matches. Apps that construct deeper cascade
    /// chains hit a <see cref="NotSupportedException"/> rather than recurse
    /// indefinitely.
    /// </summary>
    private const int MaxCascadeDepth = 32;

    /// <summary>
    /// Validates every outgoing <see cref="ForeignKey"/> on the child side
    /// after an INSERT / UPDATE / MERGE produced rows on <paramref name="childTable"/>.
    /// For each row in <paramref name="newRows"/>, every FK column tuple
    /// (full ordinals) is read; NULL in any FK column skips the check for that
    /// FK (probe-confirmed semantics). A non-NULL tuple that doesn't match any
    /// row of <see cref="ForeignKey.ReferencedTable"/> on the FK's referenced
    /// columns raises Msg 547 with the constraint name.
    /// </summary>
    private static void EnforceOutgoingForeignKeys(HeapTable childTable, IReadOnlyList<SqlValue[]> newRows, ParserContext context, string verb)
    {
        if (childTable.OutgoingForeignKeys.Count == 0)
            return;
        foreach (var fk in childTable.OutgoingForeignKeys)
        {
            if (fk.IsDisabled)
                continue;
            foreach (var newRow in newRows)
            {
                if (FkTupleHasNull(fk.ChildColumnOrdinals, newRow))
                    continue;
                if (!ReferencedRowExists(fk, newRow))
                    throw BuildChildSideViolation(fk, context, verb);
            }
        }
    }

    /// <summary>
    /// Walks every incoming <see cref="ForeignKey"/> after a DELETE removed
    /// rows of <paramref name="parentTable"/>. Each affected parent row
    /// triggers a scan of the FK's child table; the FK's delete action drives
    /// the response (NO ACTION → Msg 547, CASCADE → recursive DELETE,
    /// SET NULL → null the child's FK columns, SET DEFAULT → replace with
    /// column DEFAULT).
    /// </summary>
    /// <param name="parentTable">The parent (referenced) table whose rows were deleted.</param>
    /// <param name="affectedOldValues">Values of each affected parent row, in full-ordinal order.</param>
    /// <param name="context">Outer parser context; the cascading recursion uses the same connection / batch / undo log.</param>
    /// <param name="verb">The triggering DML verb for Msg 547 wording.</param>
    /// <param name="depth">Current cascade recursion depth — caps at <see cref="MaxCascadeDepth"/>.</param>
    /// <remarks>
    /// UPDATE on the parent goes through <see cref="EnforceIncomingFkOnUpdate"/>
    /// instead, which threads the parent's pre- and post-values together so
    /// ON UPDATE CASCADE can write the new key into each child row.
    /// </remarks>
    private static void EnforceIncomingForeignKeysOnDelete(
        HeapTable parentTable,
        List<SqlValue[]> affectedOldValues,
        ParserContext context,
        string verb,
        int depth)
    {
        if (parentTable.IncomingForeignKeys.Count == 0)
            return;
        if (depth > MaxCascadeDepth)
            throw new NotSupportedException($"FOREIGN KEY cascade depth exceeded the simulator's limit of {MaxCascadeDepth}.");

        foreach (var fk in parentTable.IncomingForeignKeys)
        {
            // Disabled FK skips both NO-ACTION reject and cascade actions —
            // probe-confirmed: DELETE on parent leaves children orphaned even
            // when the FK declared ON DELETE CASCADE.
            if (fk.IsDisabled)
                continue;
            if (affectedOldValues.Count == 0)
                continue;
            ApplyFkActionForKeySet(fk, affectedOldValues, fk.DeleteAction, context, verb, depth);
        }
    }

    private static void ApplyFkActionForKeySet(
        ForeignKey fk,
        List<SqlValue[]> affectedParentOldRows,
        ReferentialAction action,
        ParserContext context,
        string verb,
        int depth)
    {
        // Find every child row whose FK columns match one of the parent's
        // affected keys, seeking the child's FK columns per parent key when they
        // are stored (else one full scan). Materialized before any cascade
        // mutation touches the heap.
        var matchingChildRows = new List<(int PageIndex, int SlotIndex, SqlValue[] FullValues)>();
        foreach (var (pageIndex, slotIndex, childFull, _) in MatchChildRowsToParents(fk, affectedParentOldRows))
            matchingChildRows.Add((pageIndex, slotIndex, childFull));
        if (matchingChildRows.Count == 0)
            return;

        switch (action)
        {
            case ReferentialAction.NoAction:
                throw BuildParentSideViolation(fk, context, verb);
            case ReferentialAction.Cascade:
                CascadeDeleteChildRows(fk, matchingChildRows, context, depth);
                break;
            case ReferentialAction.SetNull:
                CascadeSetChildKeysToValue(fk, matchingChildRows, useDefault: false, context, depth);
                break;
            case ReferentialAction.SetDefault:
                CascadeSetChildKeysToValue(fk, matchingChildRows, useDefault: true, context, depth);
                break;
        }
    }

    private static bool FkTupleHasNull(int[] ordinals, SqlValue[] row)
    {
        foreach (var ord in ordinals)
        {
            if (row[ord].IsNull)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Probes the parent table for a row whose referenced-column tuple equals
    /// the child row's FK-column tuple. Seeks the parent's per-<see cref="Heap"/>
    /// cache on the referenced columns — always a PK/UNIQUE key, so the entry is
    /// the parent's own index — verifying each candidate against live bytes.
    /// Both of the seek's preconditions hold for every declarable foreign key,
    /// so it is the only path: a non-persisted computed column is the sole
    /// unstored kind and is refused as an FK column at declaration, and the
    /// caller has already skipped a child tuple holding a NULL.
    /// </summary>
    private static bool ReferencedRowExists(ForeignKey fk, SqlValue[] childFull) =>
        TryMapFkColumnsToStorage(fk.ReferencedTable, fk.ReferencedColumnOrdinals, out var refStorageOrdinals, out var commons)
        && TryBuildSeekProbe(childFull, fk.ChildColumnOrdinals, commons, out var probe)
            ? HeapSeekCache.For(fk.ReferencedTable.Heap)
                .AnyRowMatches(fk.ReferencedTable.Heap, fk.ReferencedTable.StoredColumns, refStorageOrdinals, commons, probe)
            : throw new InvalidOperationException($"FOREIGN KEY '{fk.Name}' has no seekable referenced-column tuple.");

    // Maps a foreign key's full column ordinals to the heap's storage ordinals
    // and resolves the per-column key type (the stored column's type) the seek
    // cache indexes by. Returns false when any FK column isn't physically stored
    // (a computed key column) — the caller then falls back to a scan.
    private static bool TryMapFkColumnsToStorage(HeapTable table, int[] fullOrdinals, out int[] storageOrdinals, out SqlType[] commons)
    {
        storageOrdinals = new int[fullOrdinals.Length];
        commons = new SqlType[fullOrdinals.Length];
        for (var i = 0; i < fullOrdinals.Length; i++)
        {
            var storage = table.StorageOrdinals[fullOrdinals[i]];
            if (storage < 0)
            {
                (storageOrdinals, commons) = ([], []);
                return false;
            }

            storageOrdinals[i] = storage;
            commons[i] = table.StoredColumns[storage].Type;
        }

        return true;
    }

    // Builds a seek probe key from one side's row values at the paired ordinals,
    // coerced to the seeked side's key types. Returns false when any value is
    // NULL — no row matches a NULL key under FK equality semantics.
    /// <summary>
    /// Builds the seek-cache probe key for <paramref name="sourceOrdinals"/>'
    /// slots of <paramref name="sourceFull"/>, coercing each component to the
    /// entry's promoted type. Returns <see langword="false"/> when any component
    /// is NULL: a NULL component never joins a seek bucket
    /// (<c>HeapSeekCache</c> drops NULL keys at build time), so the caller has
    /// to decide what a NULL means for it — no parent match for a foreign key,
    /// a fall-back to the full scan for key-uniqueness enforcement, whose
    /// NULLs-collide rule the buckets can't express.
    /// </summary>
    private static bool TryBuildSeekProbe(SqlValue[] sourceFull, int[] sourceOrdinals, SqlType[] commons, out SqlValueKey probe)
    {
        var components = new SqlValue[sourceOrdinals.Length];
        for (var i = 0; i < sourceOrdinals.Length; i++)
        {
            var value = sourceFull[sourceOrdinals[i]];
            if (value.IsNull)
            {
                probe = default;
                return false;
            }

            components[i] = value.CoerceTo(commons[i]);
        }

        probe = new SqlValueKey(components);
        return true;
    }

    // Matches child rows against a set of parent key tuples, pairing each match
    // with the index of the parent key it matched. Seeks the child's per-Heap
    // cache on the FK columns per parent key, verifying each candidate against
    // live bytes and de-duplicating by address; the child seek is index-gated by
    // nothing more than "are the columns stored," so it amortizes across
    // repeated cascades. Every FK column is stored — only a non-persisted
    // computed column isn't, and one is refused as an FK column at declaration
    // the way real refuses it (Msg 1764 from the table-level and ALTER forms,
    // Msg 8183 from the inline one; a PERSISTED computed column is allowed and
    // does have a slot). A parent key holding a NULL has no children to match,
    // which a referenced UNIQUE column reaches.
    private static IEnumerable<(int PageIndex, int SlotIndex, SqlValue[] ChildFull, int ParentIndex)> MatchChildRowsToParents(
        ForeignKey fk, List<SqlValue[]> parentKeyRows)
    {
        if (!TryMapFkColumnsToStorage(fk.ChildTable, fk.ChildColumnOrdinals, out var childStorageOrdinals, out var commons))
            throw new InvalidOperationException($"FOREIGN KEY '{fk.Name}' has a child column that is not physically stored.");

        var cache = HeapSeekCache.For(fk.ChildTable.Heap);
        var seen = new HashSet<(int, int)>();
        for (var p = 0; p < parentKeyRows.Count; p++)
        {
            if (!TryBuildSeekProbe(parentKeyRows[p], fk.ReferencedColumnOrdinals, commons, out var probe))
                continue;
            foreach (var (page, slot, bytes) in cache.MatchingRows(fk.ChildTable.Heap, fk.ChildTable.StoredColumns, childStorageOrdinals, commons, probe))
            {
                if (seen.Add((page, slot)))
                    yield return (page, slot, DecodeFullRow(fk.ChildTable, bytes), p);
            }
        }
    }

    private static SimulatedSqlException BuildChildSideViolation(ForeignKey fk, ParserContext context, string verb)
    {
        var refSchema = ResolveSchemaName(context.CurrentDatabase, fk.ReferencedTable.SchemaId);
        var refColumn = fk.ReferencedColumnOrdinals.Length == 1
            ? fk.ReferencedTable.Columns[fk.ReferencedColumnOrdinals[0]].Name
            : null;
        return SimulatedSqlException.ForeignKeyConflictOnChild(
            verb, fk.Name, DatabaseNameFor(fk.ReferencedTable), refSchema, fk.ReferencedTable.Name, refColumn, fk.IsSelfReferencing);
    }

    private static SimulatedSqlException BuildParentSideViolation(ForeignKey fk, ParserContext context, string verb)
    {
        var childSchema = ResolveSchemaName(context.CurrentDatabase, fk.ChildTable.SchemaId);
        var childColumn = fk.ChildColumnOrdinals.Length == 1
            ? fk.ChildTable.Columns[fk.ChildColumnOrdinals[0]].Name
            : null;
        return SimulatedSqlException.ForeignKeyConflictOnParent(
            verb, fk.Name, DatabaseNameFor(fk.ChildTable), childSchema, fk.ChildTable.Name, childColumn);
    }

    private static string ResolveSchemaName(Database database, int schemaId)
    {
        foreach (var entry in database.Schemas)
        {
            if (entry.Value.SchemaId == schemaId)
                return entry.Key;
        }
        return Database.DefaultSchemaName;
    }

    private static void CascadeDeleteChildRows(
        ForeignKey fk,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullValues)> matchingChildRows,
        ParserContext context,
        int depth)
    {
        var childTable = fk.ChildTable;
        var undoLog = childTable.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog;
        foreach (var (pageIndex, slotIndex, _) in matchingChildRows)
            childTable.Heap.DeleteAt(pageIndex, slotIndex, undoLog, ReclaimSuperseded(childTable, context));
        // Recurse: the just-deleted child rows may themselves be parents of
        // further FKs pointing at this child table.
        var oldRows = new List<SqlValue[]>(matchingChildRows.Count);
        foreach (var (_, _, full) in matchingChildRows)
            oldRows.Add(full);
        EnforceIncomingForeignKeysOnDelete(childTable, oldRows, context, "DELETE", depth + 1);
    }

    /// <summary>
    /// Specialized incoming-FK enforcement for UPDATE on the parent. Threads
    /// the parent's pre/post row values together so ON UPDATE CASCADE can
    /// rewrite each child row's FK columns with the parent's new value.
    /// </summary>
    private static void EnforceIncomingFkOnUpdate(
        HeapTable parentTable,
        List<(SqlValue[] OldFull, SqlValue[] NewFull)> affectedPairs,
        ParserContext context,
        int depth)
    {
        if (parentTable.IncomingForeignKeys.Count == 0)
            return;
        if (depth > MaxCascadeDepth)
            throw new NotSupportedException($"FOREIGN KEY cascade depth exceeded the simulator's limit of {MaxCascadeDepth}.");

        foreach (var fk in parentTable.IncomingForeignKeys)
        {
            if (fk.IsDisabled)
                continue;
            // Only consider parent rows whose referenced columns actually
            // changed. Identity-PK + non-PK column UPDATEs become a no-op here.
            var changedPairs = new List<(SqlValue[] Old, SqlValue[] New)>(affectedPairs.Count);
            foreach (var (oldFull, newFull) in affectedPairs)
            {
                for (var i = 0; i < fk.ReferencedColumnOrdinals.Length; i++)
                {
                    var ord = fk.ReferencedColumnOrdinals[i];
                    if (!oldFull[ord].Equals(newFull[ord]))
                    {
                        changedPairs.Add((oldFull, newFull));
                        break;
                    }
                }
            }
            if (changedPairs.Count == 0)
                continue;

            // Find matching child rows for the OLD parent keys (seek per key when
            // the FK columns are stored), pairing each with the matched key's NEW
            // parent values so the cascade can rewrite the child FK.
            var oldKeys = new List<SqlValue[]>(changedPairs.Count);
            foreach (var (oldFull, _) in changedPairs)
                oldKeys.Add(oldFull);
            var matching = new List<(int PageIndex, int SlotIndex, SqlValue[] Full, SqlValue[] ParentNew)>();
            foreach (var (pageIndex, slotIndex, childFull, parentIndex) in MatchChildRowsToParents(fk, oldKeys))
                matching.Add((pageIndex, slotIndex, childFull, changedPairs[parentIndex].New));
            if (matching.Count == 0)
                continue;

            switch (fk.UpdateAction)
            {
                case ReferentialAction.NoAction:
                    throw BuildParentSideViolation(fk, context, "UPDATE");
                case ReferentialAction.Cascade:
                    RewriteChildFkColumns(fk, matching, context, depth, mode: CascadeWriteMode.MatchParentNew);
                    break;
                case ReferentialAction.SetNull:
                    RewriteChildFkColumns(fk, matching, context, depth, mode: CascadeWriteMode.SetNull);
                    break;
                case ReferentialAction.SetDefault:
                    RewriteChildFkColumns(fk, matching, context, depth, mode: CascadeWriteMode.SetDefault);
                    break;
            }
        }
    }

    private enum CascadeWriteMode { MatchParentNew, SetNull, SetDefault }

    private static void RewriteChildFkColumns(
        ForeignKey fk,
        List<(int PageIndex, int SlotIndex, SqlValue[] Full, SqlValue[] ParentNew)> matching,
        ParserContext context,
        int depth,
        CascadeWriteMode mode)
    {
        var childTable = fk.ChildTable;
        var undoLog = childTable.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog;
        var newPairs = new List<(SqlValue[] OldFull, SqlValue[] NewFull)>(matching.Count);
        foreach (var (pageIndex, slotIndex, full, parentNew) in matching)
        {
            var oldClone = (SqlValue[])full.Clone();
            var newRow = (SqlValue[])full.Clone();
            for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
            {
                var ord = fk.ChildColumnOrdinals[i];
                newRow[ord] = mode switch
                {
                    CascadeWriteMode.MatchParentNew => parentNew[fk.ReferencedColumnOrdinals[i]],
                    CascadeWriteMode.SetNull => SqlValue.Null(childTable.Columns[ord].Type),
                    CascadeWriteMode.SetDefault => EvaluateColumnDefault(childTable.Columns[ord], context),
                    _ => throw new InvalidOperationException(),
                };
            }
            var rewritten = RowEncoder.EncodeRow(childTable.StoredColumns, ProjectStoredValues(childTable, newRow), childTable.Heap);
            if (IsLockableTable(childTable))
            {
                context.Batch.AcquireRowLockTxScoped(childTable, pageIndex, slotIndex, LockMode.Exclusive);
                context.Batch.ProbeKeyRangesForWrite(childTable, rewritten);
            }
            childTable.Heap.UpdateAt(pageIndex, slotIndex, rewritten, undoLog, ReclaimSuperseded(childTable, context));
            newPairs.Add((oldClone, newRow));
        }
        // Recurse: the child rows just got their FK columns rewritten — if
        // those columns are themselves a key referenced by another FK, that
        // FK's UPDATE action fires.
        EnforceIncomingFkOnUpdate(childTable, newPairs, context, depth + 1);
    }

    private static void CascadeSetChildKeysToValue(
        ForeignKey fk,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullValues)> matchingChildRows,
        bool useDefault,
        ParserContext context,
        int depth)
    {
        var childTable = fk.ChildTable;
        var undoLog = childTable.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog;
        var newPairs = new List<(SqlValue[] OldFull, SqlValue[] NewFull)>(matchingChildRows.Count);
        foreach (var (pageIndex, slotIndex, full) in matchingChildRows)
        {
            var oldClone = (SqlValue[])full.Clone();
            var newRow = (SqlValue[])full.Clone();
            for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
            {
                var ord = fk.ChildColumnOrdinals[i];
                newRow[ord] = useDefault
                    ? EvaluateColumnDefault(childTable.Columns[ord], context)
                    : SqlValue.Null(childTable.Columns[ord].Type);
            }
            var rewritten = RowEncoder.EncodeRow(childTable.StoredColumns, ProjectStoredValues(childTable, newRow), childTable.Heap);
            if (IsLockableTable(childTable))
            {
                context.Batch.AcquireRowLockTxScoped(childTable, pageIndex, slotIndex, LockMode.Exclusive);
                context.Batch.ProbeKeyRangesForWrite(childTable, rewritten);
            }
            childTable.Heap.UpdateAt(pageIndex, slotIndex, rewritten, undoLog, ReclaimSuperseded(childTable, context));
            newPairs.Add((oldClone, newRow));
        }
        // For SET NULL / SET DEFAULT under a DELETE on parent, the recursion
        // shape is still UPDATE on the child (the FK column changed). Use the
        // UPDATE-flavored recursion so downstream incoming FKs see the right
        // verb context.
        EnforceIncomingFkOnUpdate(childTable, newPairs, context, depth + 1);
    }

    private static SqlValue EvaluateColumnDefault(HeapColumn column, ParserContext context)
    {
        if (column.Default is null)
            return SqlValue.Null(column.Type);
        var runtime = new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch);
        return CoerceForInsert(column.Default.Run(runtime), column.Type);
    }
}
