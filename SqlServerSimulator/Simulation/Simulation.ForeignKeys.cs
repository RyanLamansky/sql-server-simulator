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
    /// Walks every incoming <see cref="ForeignKey"/> after a DELETE / UPDATE
    /// modified rows of <paramref name="parentTable"/>. Each affected parent
    /// row triggers a scan of the FK's child table; the FK's referential
    /// action drives the response (NO ACTION → Msg 547, CASCADE → recursive
    /// DELETE / UPDATE, SET NULL → null the child's FK columns, SET DEFAULT →
    /// replace with column DEFAULT).
    /// </summary>
    /// <param name="parentTable">The parent (referenced) table whose rows were affected.</param>
    /// <param name="affectedOldValues">Pre-mutation values for each affected parent row, in full-ordinal order.</param>
    /// <param name="affectedNewValues">Post-mutation values for UPDATE (same row in same position); null for DELETE.</param>
    /// <param name="context">Outer parser context; the cascading recursion uses the same connection / batch / undo log.</param>
    /// <param name="verb">The triggering DML verb (<c>DELETE</c> / <c>UPDATE</c>) for Msg 547 wording.</param>
    /// <param name="depth">Current cascade recursion depth — caps at <see cref="MaxCascadeDepth"/>.</param>
    private static void EnforceIncomingForeignKeys(
        HeapTable parentTable,
        IReadOnlyList<SqlValue[]> affectedOldValues,
        IReadOnlyList<SqlValue[]>? affectedNewValues,
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
            // UPDATE on parent: skip the FK entirely when none of the FK's
            // referenced columns actually changed value. Compares pre/post on
            // the FK's referenced ordinals; an unchanged tuple is a no-op for
            // referential integrity.
            var keyChangedRows = affectedNewValues is null
                ? affectedOldValues
                : FilterRowsWithKeyChange(fk.ReferencedColumnOrdinals, affectedOldValues, affectedNewValues);
            if (keyChangedRows.Count == 0)
                continue;

            var action = affectedNewValues is null ? fk.DeleteAction : fk.UpdateAction;
            ApplyFkActionForKeySet(fk, keyChangedRows, action, context, verb, depth);
        }
    }

    private static List<SqlValue[]> FilterRowsWithKeyChange(int[] referencedOrdinals, IReadOnlyList<SqlValue[]> oldRows, IReadOnlyList<SqlValue[]> newRows)
    {
        var result = new List<SqlValue[]>(oldRows.Count);
        for (var r = 0; r < oldRows.Count; r++)
        {
            for (var i = 0; i < referencedOrdinals.Length; i++)
            {
                var ord = referencedOrdinals[i];
                if (!oldRows[r][ord].Equals(newRows[r][ord]))
                {
                    result.Add(oldRows[r]);
                    break;
                }
            }
        }
        return result;
    }

    private static void ApplyFkActionForKeySet(
        ForeignKey fk,
        IReadOnlyList<SqlValue[]> affectedParentOldRows,
        ReferentialAction action,
        ParserContext context,
        string verb,
        int depth)
    {
        // Find every child row whose FK columns match one of the parent's
        // affected keys. Linear scan over the child table per parent key (the
        // simulator has no indexes; matches existing PK/UQ enforcement cost).
        var matchingChildRows = new List<(int PageIndex, int SlotIndex, SqlValue[] FullValues)>();
        foreach (var (rowBytes, pageIndex, slotIndex) in EnumerateChildRows(fk.ChildTable))
        {
            var childFull = DecodeFullRow(fk.ChildTable, rowBytes);
            foreach (var parentOld in affectedParentOldRows)
            {
                if (FkTuplesMatch(fk, childFull, parentOld))
                {
                    matchingChildRows.Add((pageIndex, slotIndex, childFull));
                    break;
                }
            }
        }
        if (matchingChildRows.Count == 0)
            return;

        switch (action)
        {
            case ReferentialAction.NoAction:
                throw BuildParentSideViolation(fk, context, verb);
            case ReferentialAction.Cascade:
                if (verb == "DELETE")
                    CascadeDeleteChildRows(fk, matchingChildRows, context, depth);
                else
                    CascadeUpdateChildKeys(fk, matchingChildRows, affectedParentOldRows, context, depth);
                break;
            case ReferentialAction.SetNull:
                CascadeSetChildKeysToValue(fk, matchingChildRows, useDefault: false, context, depth);
                break;
            case ReferentialAction.SetDefault:
                CascadeSetChildKeysToValue(fk, matchingChildRows, useDefault: true, context, depth);
                break;
        }
    }

    private static IEnumerable<(byte[] RowBytes, int PageIndex, int SlotIndex)> EnumerateChildRows(HeapTable child)
    {
        foreach (var (pageIndex, slotIndex, bytes) in child.Heap.EnumerateRowsWithAddress())
            yield return (bytes, pageIndex, slotIndex);
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

    private static bool FkTuplesMatch(ForeignKey fk, SqlValue[] childFull, SqlValue[] parentFull)
    {
        if (FkTupleHasNull(fk.ChildColumnOrdinals, childFull))
            return false;
        for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
        {
            var childVal = childFull[fk.ChildColumnOrdinals[i]];
            var parentVal = parentFull[fk.ReferencedColumnOrdinals[i]];
            if (parentVal.IsNull || !childVal.Equals(parentVal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Probes the parent table for a row whose referenced-column tuple equals
    /// the child row's FK-column tuple. Linear scan; returns at first match.
    /// </summary>
    private static bool ReferencedRowExists(ForeignKey fk, SqlValue[] childFull)
    {
        foreach (var rowBytes in fk.ReferencedTable.Heap.EnumerateRows())
        {
            var parentFull = DecodeFullRow(fk.ReferencedTable, rowBytes);
            var match = true;
            for (var i = 0; i < fk.ReferencedColumnOrdinals.Length; i++)
            {
                var parentVal = parentFull[fk.ReferencedColumnOrdinals[i]];
                var childVal = childFull[fk.ChildColumnOrdinals[i]];
                if (parentVal.IsNull || !parentVal.Equals(childVal))
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static SimulatedSqlException BuildChildSideViolation(ForeignKey fk, ParserContext context, string verb)
    {
        var refSchema = ResolveSchemaName(context.CurrentDatabase, fk.ReferencedTable.SchemaId);
        var refColumn = fk.ReferencedColumnOrdinals.Length == 1
            ? fk.ReferencedTable.Columns[fk.ReferencedColumnOrdinals[0]].Name
            : null;
        return SimulatedSqlException.ForeignKeyConflictOnChild(verb, fk.Name, refSchema, fk.ReferencedTable.Name, refColumn, fk.IsSelfReferencing);
    }

    private static SimulatedSqlException BuildParentSideViolation(ForeignKey fk, ParserContext context, string verb)
    {
        var childSchema = ResolveSchemaName(context.CurrentDatabase, fk.ChildTable.SchemaId);
        var childColumn = fk.ChildColumnOrdinals.Length == 1
            ? fk.ChildTable.Columns[fk.ChildColumnOrdinals[0]].Name
            : null;
        return SimulatedSqlException.ForeignKeyConflictOnParent(verb, fk.Name, childSchema, fk.ChildTable.Name, childColumn);
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
            childTable.Heap.DeleteAt(pageIndex, slotIndex, undoLog);
        // Recurse: the just-deleted child rows may themselves be parents of
        // further FKs pointing at this child table.
        var oldRows = new List<SqlValue[]>(matchingChildRows.Count);
        foreach (var (_, _, full) in matchingChildRows)
            oldRows.Add(full);
        EnforceIncomingForeignKeys(childTable, oldRows, affectedNewValues: null, context, "DELETE", depth + 1);
    }

    private static void CascadeUpdateChildKeys(
        ForeignKey fk,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullValues)> matchingChildRows,
        IReadOnlyList<SqlValue[]> affectedParentOldRows,
        ParserContext context,
        int depth)
    {
        // ON UPDATE CASCADE: rewrite the child's FK columns to match the
        // updated parent value. The new parent values for each old parent
        // tuple are inferred from the caller's affectedNewValues; but the
        // ApplyFkActionForKeySet entry point doesn't carry those forward —
        // for the UPDATE path we re-find the parent's new value by scanning
        // the old/new arrays from the caller via a closure pattern (avoided
        // here for simplicity). Real-world ON UPDATE CASCADE chains touch
        // PK/UQ columns only — and SQL Server's most common deployment uses
        // surrogate identity PKs that aren't updated. The simulator's UPDATE
        // path passes the parent's new tuple through a captured side-channel
        // (see <see cref="EnforceIncomingFkOnUpdate"/>).
        throw new NotSupportedException("ON UPDATE CASCADE through the EnforceIncomingForeignKeys entry isn't reachable — use EnforceIncomingFkOnUpdate which threads the new values explicitly.");
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

            // Find matching child rows for the OLD parent keys.
            var matching = new List<(int PageIndex, int SlotIndex, SqlValue[] Full, SqlValue[] ParentNew)>();
            foreach (var (rowBytes, pageIndex, slotIndex) in EnumerateChildRows(fk.ChildTable))
            {
                var childFull = DecodeFullRow(fk.ChildTable, rowBytes);
                foreach (var (oldFull, newFull) in changedPairs)
                {
                    if (FkTuplesMatch(fk, childFull, oldFull))
                    {
                        matching.Add((pageIndex, slotIndex, childFull, newFull));
                        break;
                    }
                }
            }
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
            childTable.Heap.DeleteAt(pageIndex, slotIndex, undoLog);
            childTable.Heap.Insert(RowEncoder.EncodeRow(childTable.StoredColumns, ProjectStoredValues(childTable, newRow), childTable.Heap), undoLog);
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
            childTable.Heap.DeleteAt(pageIndex, slotIndex, undoLog);
            childTable.Heap.Insert(RowEncoder.EncodeRow(childTable.StoredColumns, ProjectStoredValues(childTable, newRow), childTable.Heap), undoLog);
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
