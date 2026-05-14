using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>ALTER TABLE … ADD [COLUMN] col1 TYPE [constraints], col2 TYPE
    /// [constraints], …</c>. Cursor on entry is the first non-CONSTRAINT
    /// token after <c>ADD</c> (typically the column-name identifier; an
    /// optional <c>COLUMN</c> keyword is accepted and skipped). Routed from
    /// <see cref="TryParseAlterTableAddConstraint"/> when the post-ADD token
    /// isn't a constraint keyword.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Column definition grammar is the same as <c>CREATE TABLE</c>'s column-
    /// list path — type with optional length / scale; any combination of
    /// <c>IDENTITY</c> / <c>NULL</c>|<c>NOT NULL</c> / <c>DEFAULT</c> /
    /// <c>PRIMARY KEY</c>|<c>UNIQUE</c> / <c>CHECK</c> / <c>REFERENCES</c> /
    /// <c>CONSTRAINT</c>-name forms; computed columns via <c>name AS expr</c>.
    /// Shared via <see cref="ParseOneColumnIntoLists"/>.
    /// </para>
    /// <para>
    /// Apply phase enforces Msg 2705 (duplicate column name on the table) and
    /// Msg 4901 (NOT NULL without DEFAULT / IDENTITY / ROWVERSION on a non-
    /// empty table). New PK / UNIQUE / CHECK / FK declarations are resolved
    /// via the same pipelines CREATE TABLE uses, with full ordinals shifted
    /// by the existing column count. After the schema mutation, every row
    /// in the heap is re-encoded against the new schema — the simulator
    /// goes the eager rewrite path regardless of column nullability or
    /// DEFAULT shape, which keeps <see cref="RowDecoder"/> simple at the
    /// cost of an O(rows) scan (real SQL Server has a metadata-only
    /// optimization for many shapes — documented as a fidelity gap).
    /// </para>
    /// </remarks>
    private static bool ParseAddColumns(ParserContext context, MultiPartName tableName)
    {
        // Optional COLUMN keyword (probe-confirmed real SQL Server accepts
        // both `ADD col TYPE` and `ADD COLUMN col TYPE`). COLUMN is a reserved
        // keyword in the simulator's grammar.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Column })
            context.MoveNextRequired();

        var heapColumns = new List<HeapColumn?>();
        var explicitNull = new List<bool>();
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)>();
        var pendingChecks = new List<(string? Name, BooleanExpression Predicate, string? InlineColumn)>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable)>();
        var pendingForeignKeys = new List<PendingForeignKey>();

        HeapTable? table = null;
        var existingIdentityCount = 0;
        if (!context.Batch.IsSkipping)
        {
            if (!context.Batch.TryResolveTable(tableName, out table))
                throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
            existingIdentityCount = table.IdentityOrdinal >= 0 ? 1 : 0;
        }
        var identityCount = existingIdentityCount;

        while (true)
        {
            ParseOneColumnIntoLists(
                context,
                table?.Name ?? tableName.Leaf,
                isTableVariable: false,
                isTableType: false,
                heapColumns,
                explicitNull,
                pendingKeys,
                pendingChecks,
                pendingComputed,
                pendingPeriod: null,
                pendingForeignKeys,
                ref identityCount);

            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }

        if (context.Batch.IsSkipping || table is null)
            return true;

        var tableIsEmpty = true;
        foreach (var _ in table.Heap.EnumerateRows())
        {
            tableIsEmpty = false;
            break;
        }

        if (pendingComputed.Count > 0)
            ResolveComputedColumnsForAddColumn(table, heapColumns, pendingComputed);

        var existingCount = table.Columns.Length;
        var newColumns = new HeapColumn[heapColumns.Count];
        for (var i = 0; i < heapColumns.Count; i++)
        {
            if (heapColumns[i] is null)
                throw new InvalidOperationException($"Computed-column resolution left index {i} unresolved.");
            newColumns[i] = heapColumns[i]!;
        }

        var qualifiedTableName = $"{Database.DefaultSchemaName}.{table.Name}";
        for (var i = 0; i < newColumns.Length; i++)
        {
            foreach (var existing in table.Columns)
            {
                if (Collation.Default.Equals(existing.Name, newColumns[i].Name))
                    throw SimulatedSqlException.ColumnNamesMustBeUnique(newColumns[i].Name, qualifiedTableName);
            }
            for (var j = 0; j < i; j++)
            {
                if (Collation.Default.Equals(newColumns[j].Name, newColumns[i].Name))
                    throw SimulatedSqlException.ColumnNamesMustBeUnique(newColumns[i].Name, qualifiedTableName);
            }
        }

        if (!tableIsEmpty)
        {
            foreach (var c in newColumns)
            {
                if (!c.IsStored)
                    continue;
                if (c.Nullable || c.Default is not null || c.Identity is not null || c.Type == SqlType.RowVersion)
                    continue;
                throw SimulatedSqlException.AddColumnRequiresDefaultOrNullable(c.Name, table.Name);
            }
        }

        // Shift PK / UQ FullOrdinals to the combined-column index space.
        var shiftedKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)>();
        foreach (var k in pendingKeys)
        {
            var shifted = new int[k.FullOrdinals.Length];
            for (var i = 0; i < k.FullOrdinals.Length; i++)
                shifted[i] = k.FullOrdinals[i] + existingCount;
            shiftedKeys.Add((k.Kind, k.Name, shifted));
        }

        var originalColumns = table.Columns;
        var originalKeyCount = table.KeyConstraints.Count;
        var originalCheckCount = table.CheckConstraints.Count;
        var originalFkCount = table.OutgoingForeignKeys.Count;

        var combined = new HeapColumn[existingCount + newColumns.Length];
        Array.Copy(table.Columns, combined, existingCount);
        Array.Copy(newColumns, 0, combined, existingCount, newColumns.Length);
        table.Columns = combined;
        table.RecomputeStorageProjections();

        try
        {
            if (shiftedKeys.Count > 0)
            {
                var resolved = ResolveKeyConstraints(table.Name, combined, shiftedKeys, context.CurrentDatabase);
                foreach (var kc in resolved)
                    table.KeyConstraints.Add(kc);
            }

            if (pendingChecks.Count > 0)
            {
                var checks = ResolveCheckConstraints(table.Name, pendingChecks, context.CurrentDatabase);
                foreach (var ck in checks)
                    table.CheckConstraints.Add(ck);
            }

            if (pendingForeignKeys.Count > 0)
            {
                var shiftedForeignKeys = new List<PendingForeignKey>();
                foreach (var pf in pendingForeignKeys)
                {
                    var shifted = new int[pf.ChildFullOrdinals.Length];
                    for (var i = 0; i < shifted.Length; i++)
                        shifted[i] = pf.ChildFullOrdinals[i] + existingCount;
                    shiftedForeignKeys.Add(pf with { ChildFullOrdinals = shifted });
                }
                ResolveForeignKeys(table, shiftedForeignKeys, context);
            }

            RewriteHeapForAddColumns(table, newColumns, existingCount, context);
        }
        catch
        {
            // Roll back partial schema mutation on any post-mutate failure.
            table.Columns = originalColumns;
            table.RecomputeStorageProjections();
            while (table.KeyConstraints.Count > originalKeyCount)
                table.KeyConstraints.RemoveAt(table.KeyConstraints.Count - 1);
            while (table.CheckConstraints.Count > originalCheckCount)
                table.CheckConstraints.RemoveAt(table.CheckConstraints.Count - 1);
            while (table.OutgoingForeignKeys.Count > originalFkCount)
            {
                var stale = table.OutgoingForeignKeys[^1];
                table.OutgoingForeignKeys.RemoveAt(table.OutgoingForeignKeys.Count - 1);
                _ = stale.ReferencedTable.IncomingForeignKeys.Remove(stale);
            }
            throw;
        }

        return true;
    }

    private static void ResolveComputedColumnsForAddColumn(
        HeapTable table,
        List<HeapColumn?> heapColumns,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable)> pendingComputed)
    {
        SqlType ResolveReference(MultiPartName reference)
        {
            foreach (var existing in table.Columns)
            {
                if (Collation.Default.Equals(existing.Name, reference.Leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, table.Name)
                        : existing.Type;
                }
            }
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } sibling && Collation.Default.Equals(sibling.Name, reference.Leaf))
                {
                    return sibling.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(sibling.Name, table.Name)
                        : sibling.Type;
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        foreach (var pc in pendingComputed)
        {
            var computedType = pc.Expression.GetSqlType(ResolveReference);
            heapColumns[pc.Index] = new HeapColumn(pc.Name, computedType, maxLength: null, nullable: pc.Nullable, computedExpression: pc.Expression, isPersisted: pc.Persisted);
        }
    }

    /// <summary>
    /// Re-encodes every row in <paramref name="table"/>'s heap against the
    /// post-ADD schema. New columns get NULL (when nullable) or their
    /// DEFAULT-evaluated value (when NOT NULL) or a per-row identity value
    /// (when IDENTITY) — matches SQL Server's probe-confirmed semantic that
    /// DEFAULT on a nullable ADD does NOT backfill, only the NOT NULL form
    /// does. After the rewrite, <paramref name="table"/>'s old <c>Heap</c>
    /// is discarded.
    /// </summary>
    private static void RewriteHeapForAddColumns(HeapTable table, HeapColumn[] newColumns, int existingCount, ParserContext context)
    {
        var anyRows = false;
        foreach (var _ in table.Heap.EnumerateRows())
        {
            anyRows = true;
            break;
        }
        if (!anyRows)
            return;

        // Pre-evaluate NOT-NULL DEFAULT expressions once (constant snapshot —
        // probe-confirmed that GETDATE() in a DEFAULT backfill produces a
        // single timestamp for every existing row).
        var backfillValues = new SqlValue?[newColumns.Length];
        static SqlValue ResolveNothing(MultiPartName reference) => throw SimulatedSqlException.InvalidColumnName(reference);
        var batch = context.Batch;
        var runtime = new RuntimeContext(ResolveNothing, batch);
        for (var i = 0; i < newColumns.Length; i++)
        {
            var c = newColumns[i];
            if (!c.IsStored)
                continue;
            if (c.Nullable)
            {
                backfillValues[i] = SqlValue.Null(c.Type);
                continue;
            }
            if (c.Identity is not null || c.Type == SqlType.RowVersion)
                continue;
            if (c.Default is { } defaultExpr)
                backfillValues[i] = defaultExpr.Run(runtime).CoerceTo(c.Type);
        }

        // Pre-compute the pre-add stored column array (the layout the old
        // rows were encoded against). Walking table.Columns[0..existingCount]
        // captures it — same array reference would also do since the new
        // columns appended after.
        var preAddStoredCount = 0;
        for (var i = 0; i < existingCount; i++)
        {
            if (table.Columns[i].IsStored)
                preAddStoredCount++;
        }
        var preAddStoredColumns = new HeapColumn[preAddStoredCount];
        var s = 0;
        for (var i = 0; i < existingCount; i++)
        {
            if (table.Columns[i].IsStored)
                preAddStoredColumns[s++] = table.Columns[i];
        }

        var oldHeap = table.Heap;
        var newHeap = new Heap();
        var newStoredColumns = table.StoredColumns;

        foreach (var oldBytes in oldHeap.EnumerateRows())
        {
            var newStoredValues = new SqlValue[newStoredColumns.Length];
            for (var i = 0; i < preAddStoredCount; i++)
                newStoredValues[i] = RowDecoder.DecodeColumn(preAddStoredColumns, oldBytes, i, oldHeap);

            var newStorageIndex = preAddStoredCount;
            for (var i = 0; i < newColumns.Length; i++)
            {
                var c = newColumns[i];
                if (!c.IsStored)
                    continue;
                newStoredValues[newStorageIndex] = c.Identity is { } identity
                    ? CoerceForIdentity(identity.GenerateNext(), c)
                    : c.Type == SqlType.RowVersion
                        ? SqlValue.FromRowVersion(context.CurrentDatabase.AllocateRowVersion())
                        : backfillValues[i] ?? SqlValue.Null(c.Type);
                newStorageIndex++;
            }

            var encoded = RowEncoder.EncodeRow(newStoredColumns, newStoredValues, newHeap);
            newHeap.Insert(encoded);
        }

        table.Heap = newHeap;
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … DROP COLUMN [IF EXISTS] col1 [, col2, …]</c>.
    /// Cursor on entry: the <c>COLUMN</c> contextual keyword. Two-pass apply:
    /// every name is resolved + dependency-checked before any mutation, so
    /// a single Msg 5074 or Msg 4924 leaves the table unchanged. The
    /// dependency walker enumerates PK / UQ → outgoing FK → incoming FK →
    /// CHECK (inline + table-level by name walk) → DEFAULT → index; the
    /// resulting Msg 5074 lists every blocker on its own line with the
    /// appropriate <c>"The object 'X'"</c> / <c>"The index 'X'"</c> prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After clearing, columns are removed in declaration order; surviving
    /// constraints / indexes / FKs have their column ordinals remapped in
    /// place (the storage-ordinal array elements mutate; the constraint
    /// objects themselves stay), the heap is re-encoded against the new
    /// schema, and any column-attached <see cref="DefaultConstraint"/>
    /// disappears with its column (no separate DROP DEFAULT needed).
    /// </para>
    /// </remarks>
    private static bool ParseDropColumns(ParserContext context, MultiPartName tableName)
    {
        var ifExists = false;
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ifExists = true;
            context.MoveNextRequired();
        }

        var names = new List<string>();
        if (context.Token is not Name firstName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        names.Add(firstName.Value);

        context.MoveNextOptional();
        while (context.Token is Operator { Character: ',' })
        {
            if (context.GetNextRequired() is not Name next)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(next.Value);
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

        var toDropOrdinals = new List<int>();
        foreach (var name in names)
        {
            var ordinal = -1;
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (Collation.Default.Equals(table.Columns[i].Name, name))
                {
                    ordinal = i;
                    break;
                }
            }
            if (ordinal < 0)
            {
                if (ifExists)
                    continue;
                throw SimulatedSqlException.DropColumnDoesNotExist(name, table.Name);
            }
            if (toDropOrdinals.Contains(ordinal))
                throw SimulatedSqlException.DropColumnDoesNotExist(name, table.Name);
            toDropOrdinals.Add(ordinal);
        }

        if (toDropOrdinals.Count == 0)
            return true;

        // Per-column dependency check. Msg 5074 lists every blocker; the
        // probe-confirmed format uses one line per blocker with the
        // "The object 'X'" / "The index 'X'" prefix per kind.
        foreach (var ordinal in toDropOrdinals)
        {
            var col = table.Columns[ordinal];
            var blockers = CollectColumnDependencies(table, ordinal, col);
            if (blockers.Count > 0)
                throw SimulatedSqlException.DropColumnHasDependenciesMixed(col.Name, blockers);
        }

        // Apply phase. Build full-ordinal and storage-ordinal mappings
        // (old → new), then remap every surviving constraint / index / FK,
        // rewrite the heap projecting surviving storage slots, and swap the
        // table's Columns array.
        var keep = new bool[table.Columns.Length];
        for (var i = 0; i < table.Columns.Length; i++)
            keep[i] = true;
        foreach (var o in toDropOrdinals)
            keep[o] = false;

        var oldFullToNew = new int[table.Columns.Length];
        var n = 0;
        for (var i = 0; i < table.Columns.Length; i++)
            oldFullToNew[i] = keep[i] ? n++ : -1;

        var oldStorageColumns = table.StoredColumns;
        var oldStorageOrdinals = (int[])table.StorageOrdinals.Clone();
        var oldStorageToNew = new int[oldStorageColumns.Length];
        var newStorageIndex = 0;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            var storeOrd = oldStorageOrdinals[i];
            if (storeOrd < 0)
                continue;
            oldStorageToNew[storeOrd] = keep[i] ? newStorageIndex++ : -1;
        }

        // Remap surviving constraint/index/FK ordinals in place.
        foreach (var kc in table.KeyConstraints)
        {
            for (var i = 0; i < kc.StorageOrdinals.Length; i++)
                kc.StorageOrdinals[i] = oldStorageToNew[kc.StorageOrdinals[i]];
        }
        foreach (var ix in table.Indexes)
        {
            for (var i = 0; i < ix.KeyColumns.Length; i++)
            {
                var keyCol = ix.KeyColumns[i];
                ix.KeyColumns[i] = new IndexKeyColumn(oldStorageToNew[keyCol.StorageOrdinal], keyCol.IsDescending);
            }
            for (var i = 0; i < ix.IncludedColumns.Length; i++)
                ix.IncludedColumns[i] = oldStorageToNew[ix.IncludedColumns[i]];
        }
        foreach (var fk in table.OutgoingForeignKeys)
        {
            for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
                fk.ChildColumnOrdinals[i] = oldFullToNew[fk.ChildColumnOrdinals[i]];
        }
        foreach (var fk in table.IncomingForeignKeys)
        {
            for (var i = 0; i < fk.ReferencedColumnOrdinals.Length; i++)
                fk.ReferencedColumnOrdinals[i] = oldFullToNew[fk.ReferencedColumnOrdinals[i]];
        }

        // Rewrite heap rows projecting surviving storage slots.
        RewriteHeapForDropColumns(table, oldStorageColumns, oldStorageToNew, newStorageIndex);

        var newColumns = new HeapColumn[n];
        var c2 = 0;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (keep[i])
                newColumns[c2++] = table.Columns[i];
        }
        table.Columns = newColumns;
        table.RecomputeStorageProjections();

        return true;
    }

    /// <summary>
    /// Walks every constraint / index / FK on the table and reports the
    /// names of any that reference the column at <paramref name="ordinal"/>.
    /// Returns an ordered list of (name, isIndex) entries the Msg 5074
    /// factory consumes to render the probed message shape. Walker order:
    /// PK / UQ → outgoing FK → incoming FK (parent-side references to
    /// this column) → CHECK (inline by name, table-level by predicate
    /// walk) → DEFAULT → index.
    /// </summary>
    private static List<(string Name, bool IsIndex)> CollectColumnDependencies(HeapTable table, int ordinal, HeapColumn col)
    {
        var blockers = new List<(string, bool)>();
        var storageOrdinal = table.StorageOrdinals[ordinal];

        foreach (var kc in table.KeyConstraints)
        {
            if (storageOrdinal >= 0)
            {
                foreach (var so in kc.StorageOrdinals)
                {
                    if (so == storageOrdinal)
                    {
                        blockers.Add((kc.Name, false));
                        break;
                    }
                }
            }
        }
        foreach (var fk in table.OutgoingForeignKeys)
        {
            foreach (var co in fk.ChildColumnOrdinals)
            {
                if (co == ordinal)
                {
                    blockers.Add((fk.Name, false));
                    break;
                }
            }
        }
        foreach (var fk in table.IncomingForeignKeys)
        {
            foreach (var ro in fk.ReferencedColumnOrdinals)
            {
                if (ro == ordinal)
                {
                    blockers.Add((fk.Name, false));
                    break;
                }
            }
        }
        foreach (var ck in table.CheckConstraints)
        {
            if (ck.InlineColumn is not null && Collation.Default.Equals(ck.InlineColumn, col.Name))
            {
                blockers.Add((ck.Name, false));
                continue;
            }
            if (CheckPredicateReferencesColumn(ck.Predicate, col.Name))
                blockers.Add((ck.Name, false));
        }
        if (col.DefaultConstraint is { } df)
            blockers.Add((df.Name, false));
        foreach (var ix in table.Indexes)
        {
            if (storageOrdinal < 0)
                continue;
            var referenced = false;
            foreach (var keyCol in ix.KeyColumns)
            {
                if (keyCol.StorageOrdinal == storageOrdinal)
                {
                    referenced = true;
                    break;
                }
            }
            if (!referenced)
            {
                foreach (var inc in ix.IncludedColumns)
                {
                    if (inc == storageOrdinal)
                    {
                        referenced = true;
                        break;
                    }
                }
            }
            if (referenced)
                blockers.Add((ix.Name, true));
        }

        return blockers;
    }

    /// <summary>
    /// Returns true when the given CHECK predicate references a column
    /// by name. Walks the expression tree structurally — same shape the
    /// inline-CHECK peer-reference walker uses at CREATE TABLE.
    /// </summary>
    private static bool CheckPredicateReferencesColumn(BooleanExpression predicate, string columnName)
    {
        var found = false;
        predicate.VisitOperandExpressions(operand =>
        {
            if (found)
                return;
            operand.VisitColumnReferences(reference =>
            {
                if (!found && Collation.Default.Equals(reference.Leaf, columnName))
                    found = true;
            });
        });
        return found;
    }

    private static void RewriteHeapForDropColumns(HeapTable table, HeapColumn[] oldStoredColumns, int[] oldStorageToNew, int newStoredCount)
    {
        var anyRows = false;
        foreach (var _ in table.Heap.EnumerateRows())
        {
            anyRows = true;
            break;
        }
        if (!anyRows)
            return;

        var newStoredColumns = new HeapColumn[newStoredCount];
        for (var i = 0; i < oldStoredColumns.Length; i++)
        {
            var mapped = oldStorageToNew[i];
            if (mapped >= 0)
                newStoredColumns[mapped] = oldStoredColumns[i];
        }

        var oldHeap = table.Heap;
        var newHeap = new Heap();
        foreach (var oldBytes in oldHeap.EnumerateRows())
        {
            var newStoredValues = new SqlValue[newStoredCount];
            for (var i = 0; i < oldStoredColumns.Length; i++)
            {
                var mapped = oldStorageToNew[i];
                if (mapped < 0)
                    continue;
                newStoredValues[mapped] = RowDecoder.DecodeColumn(oldStoredColumns, oldBytes, i, oldHeap);
            }
            var encoded = RowEncoder.EncodeRow(newStoredColumns, newStoredValues, newHeap);
            newHeap.Insert(encoded);
        }

        table.Heap = newHeap;
    }
}
