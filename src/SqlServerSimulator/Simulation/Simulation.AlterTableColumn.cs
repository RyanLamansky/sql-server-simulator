using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The column definitions of one <c>ALTER TABLE … ADD</c> list, collected
    /// element by element while <see cref="TryParseAlterTableAddConstraint"/>
    /// walks the list and applied together by <see cref="ApplyAddedColumns"/>
    /// before any constraint element of the list runs.
    /// </summary>
    /// <remarks>
    /// Column definition grammar is the same as <c>CREATE TABLE</c>'s column-
    /// list path — type with optional length / scale; any combination of
    /// <c>IDENTITY</c> / <c>NULL</c>|<c>NOT NULL</c> / <c>DEFAULT</c> /
    /// <c>PRIMARY KEY</c>|<c>UNIQUE</c> / <c>CHECK</c> / <c>REFERENCES</c> /
    /// <c>CONSTRAINT</c>-name forms; computed columns via <c>name AS expr</c>.
    /// Shared via <see cref="ParseOneColumnIntoLists"/>.
    /// </remarks>
    private sealed class AddedColumns
    {
        public readonly HeapTable Table;
        public readonly List<HeapColumn?> HeapColumns = [];
        public readonly List<bool> ExplicitNull = [];
        public readonly List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)> PendingKeys = [];
        public readonly List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)> PendingChecks = [];
        public readonly List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> PendingComputed = [];
        public readonly List<PendingForeignKey> PendingForeignKeys = [];
        public readonly List<int> WithValuesColumns = [];
        private int identityCount;

        public AddedColumns(ParserContext context, MultiPartName tableName)
        {
            // Resolved in skip mode too, for the ordinals a column error names.
            // A missing table defers the statement there (Msg 4902 is a
            // deferrable name error), so no column is checked before it.
            if (!context.Batch.TryResolveTable(tableName, out var table))
                throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
            this.Table = table;
            this.identityCount = table.IdentityOrdinal >= 0 ? 1 : 0;
        }

        /// <summary>Parses one column definition, leaving the cursor on the token after it.</summary>
        public void ParseOne(ParserContext context) =>
            ParseOneColumnIntoLists(
                context,
                this.Table.Name,
                isTableVariable: false,
                isTableType: false,
                this.HeapColumns,
                this.ExplicitNull,
                this.PendingKeys,
                this.PendingChecks,
                this.PendingComputed,
                pendingPeriod: null,
                this.PendingForeignKeys,
                ref this.identityCount,
                withValuesColumns: this.WithValuesColumns,
                ordinalOffset: this.Table.Columns.Length);
    }

    /// <summary>
    /// Adds the columns an <c>ALTER TABLE … ADD</c> list defined, with their
    /// inline constraints, and re-encodes every row against the widened
    /// schema. A column the list's table-level <c>PRIMARY KEY</c> names
    /// (<paramref name="primaryKeyColumns"/>) is NOT NULL unless declared
    /// <c>NULL</c>, as <c>CREATE TABLE</c> promotes one (probed 2026-09-25
    /// against SQL Server 2025). Returns what puts the table's columns and
    /// rows back if a later element of the list fails; a failure here puts
    /// them back itself.
    /// </summary>
    /// <remarks>
    /// Enforces Msg 2705 (duplicate column name on the table) and Msg 4901
    /// (NOT NULL without DEFAULT / IDENTITY / ROWVERSION on a non-empty
    /// table). New PK / UNIQUE / CHECK / FK declarations are resolved via the
    /// same pipelines CREATE TABLE uses, with full ordinals shifted by the
    /// existing column count. The rewrite is eager regardless of column
    /// nullability or DEFAULT shape, which keeps <see cref="RowDecoder"/>
    /// simple at the cost of an O(rows) scan (real has a metadata-only
    /// optimization for many shapes).
    /// </remarks>
    private static AddedColumnsUndo ApplyAddedColumns(ParserContext context, AddedColumns added, List<string>? primaryKeyColumns)
    {
        var table = added.Table;
        var heapColumns = added.HeapColumns;
        var pendingChecks = added.PendingChecks;
        var collation = context.Batch.CurrentDatabase.Collation;
        var tableIsEmpty = true;
        foreach (var _ in table.Heap.EnumerateRows())
        {
            tableIsEmpty = false;
            break;
        }

        if (added.PendingComputed.Count > 0)
            ResolveComputedColumnsForAddColumn(context.Batch, collation, table, heapColumns, added.PendingComputed);

        if (primaryKeyColumns is not null)
        {
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { Nullable: true } column && !added.ExplicitNull[i] && primaryKeyColumns.Exists(name => collation.Equals(name, column.Name)))
                    heapColumns[i] = WithNotNull(column);
            }
        }

        // An added column's inline CHECK may not read a non-persisted computed
        // column — Msg 1764, over the pre-existing columns as well as the ones
        // this statement adds.
        if (pendingChecks.Count > 0)
        {
            var visibleColumns = new List<HeapColumn?>(table.Columns.Length + heapColumns.Count);
            visibleColumns.AddRange(table.Columns);
            visibleColumns.AddRange(heapColumns);
            BindCheckConstraints(context.Batch, visibleColumns, pendingChecks);
            RejectChecksOverNonPersistedComputedColumns(collation, table.Name, visibleColumns, pendingChecks);
        }

        var existingCount = table.Columns.Length;
        var newColumns = new HeapColumn[heapColumns.Count];
        for (var i = 0; i < heapColumns.Count; i++)
        {
            if (heapColumns[i] is null)
                throw new InvalidOperationException($"Computed-column resolution left index {i} unresolved.");
            newColumns[i] = heapColumns[i]!;
        }

        // A table holds one ROWGUIDCOL; adding a second is Msg 8196 state 16.
        if (Array.Exists(newColumns, column => column.IsRowGuidCol) && Array.Exists(table.Columns, column => column.IsRowGuidCol))
            throw SimulatedSqlException.MultipleRowGuidColumns(16);

        // State 4 for a name the table already has, 3 for one the list repeats
        // (probed 2026-09-25 against SQL Server 2025).
        for (var i = 0; i < newColumns.Length; i++)
        {
            foreach (var existing in table.Columns)
            {
                if (collation.Equals(existing.Name, newColumns[i].Name))
                    throw SimulatedSqlException.DuplicateColumnInTable(newColumns[i].Name, table.Name, 4);
            }
            for (var j = 0; j < i; j++)
            {
                if (collation.Equals(newColumns[j].Name, newColumns[i].Name))
                    throw SimulatedSqlException.DuplicateColumnInTable(newColumns[i].Name, table.Name, 3);
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
        var shiftedKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)>();
        foreach (var k in added.PendingKeys)
        {
            var shifted = new int[k.FullOrdinals.Length];
            for (var i = 0; i < k.FullOrdinals.Length; i++)
                shifted[i] = k.FullOrdinals[i] + existingCount;
            shiftedKeys.Add((k.Kind, k.Name, shifted, k.Clustered, k.IgnoreDupKey, k.Descending));
        }

        var undo = new AddedColumnsUndo(table);
        var originalKeyCount = table.KeyConstraints.Count;
        var originalCheckCount = table.CheckConstraints.Count;
        var originalFkCount = table.OutgoingForeignKeys.Count;

        var combined = new HeapColumn[existingCount + newColumns.Length];
        Array.Copy(table.Columns, combined, existingCount);
        Array.Copy(newColumns, 0, combined, existingCount, newColumns.Length);
        table.Columns = combined;
        table.RecomputeStorageProjections();
        // Each added column takes the next id past the watermark, so it never
        // reuses the id of a previously dropped column (probe-confirmed).
        table.AssignColumnIds();

        try
        {
            if (shiftedKeys.Count > 0)
            {
                var resolved = ResolveKeyConstraints(table.Name, combined, shiftedKeys, context.CurrentDatabase, context.Batch.CurrentStatement.UtcNow);
                foreach (var kc in resolved)
                    table.KeyConstraints.Add(kc);
            }

            if (pendingChecks.Count > 0)
            {
                var checks = ResolveCheckConstraints(table.Name, pendingChecks, context.CurrentDatabase, context.Batch.CurrentStatement.UtcNow);
                foreach (var ck in checks)
                    table.CheckConstraints.Add(ck);
            }

            if (added.PendingForeignKeys.Count > 0)
            {
                var shiftedForeignKeys = new List<PendingForeignKey>();
                foreach (var pf in added.PendingForeignKeys)
                {
                    var shifted = new int[pf.ChildFullOrdinals.Length];
                    for (var i = 0; i < shifted.Length; i++)
                        shifted[i] = pf.ChildFullOrdinals[i] + existingCount;
                    shiftedForeignKeys.Add(pf.WithChildFullOrdinals(shifted));
                }
                ResolveForeignKeys(table, shiftedForeignKeys, context);
            }

            RewriteHeapForAddColumns(table, newColumns, existingCount, added.WithValuesColumns, context);
        }
        catch
        {
            // Roll back partial schema mutation on any post-mutate failure —
            // including the watermark, so a failed ADD doesn't consume ids.
            undo.Restore();
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

        return undo;
    }

    /// <summary>
    /// What <see cref="ApplyAddedColumns"/> changed about the table itself —
    /// its column array, the column-id watermark and its rows, which the
    /// rewrite moved to a fresh <see cref="Heap"/> — so a later element of
    /// the same ADD list that fails can put them back. The constraint lists
    /// are the caller's to restore.
    /// </summary>
    private sealed class AddedColumnsUndo(HeapTable table)
    {
        private readonly HeapColumn[] columns = table.Columns;
        private readonly int maxColumnId = table.MaxColumnIdUsed;
        private readonly Heap heap = table.Heap;

        public void Restore()
        {
            table.Columns = this.columns;
            table.RecomputeStorageProjections();
            table.MaxColumnIdUsed = this.maxColumnId;
            table.Heap = this.heap;
        }
    }

    private static void ResolveComputedColumnsForAddColumn(
        BatchContext batch,
        Collation collation,
        HeapTable table,
        List<HeapColumn?> heapColumns,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed)
    {
        SqlType ResolveReference(MultiPartName reference)
        {
            foreach (var existing in table.Columns)
            {
                if (collation.Equals(existing.Name, reference.Leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, table.Name)
                        : existing.Type;
                }
            }
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } sibling && collation.Equals(sibling.Name, reference.Leaf))
                {
                    return sibling.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(sibling.Name, table.Name)
                        : sibling.Type;
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        // Same rule CREATE TABLE applies: the expression's own nullability is
        // the column's.
        bool ResolveReferenceNullable(MultiPartName reference)
        {
            foreach (var existing in table.Columns)
            {
                if (collation.Equals(existing.Name, reference.Leaf))
                    return existing.Nullable;
            }
            foreach (var sibling in heapColumns)
            {
                if (sibling is not null && collation.Equals(sibling.Name, reference.Leaf))
                    return sibling.Nullable;
            }
            return true;
        }

        foreach (var pc in pendingComputed)
        {
            // Same PERSISTED gate CREATE TABLE applies, under ALTER's own verb
            // (probe-confirmed Msg 1934 "ALTER TABLE failed …").
            if (pc.Persisted && IncorrectSetOptionNames(batch.Parser) is { } setOptions)
                throw SimulatedSqlException.IncorrectSetOptions("ALTER TABLE", setOptions);
            if (pc.Persisted)
            {
                var scope = new List<HeapColumn?>(table.Columns.Length + heapColumns.Count);
                scope.AddRange(table.Columns);
                scope.AddRange(heapColumns);
                RejectNondeterministicPersisted(batch.Parser, scope, pc.Name, table.Name, pc.Definition);
            }
            var computedType = pc.Expression.GetSqlType(batch, ResolveReference);
            var inferredNullable = pc.Expression.ResultIsNullable(new NullabilityContext(batch, ResolveReferenceNullable, ResolveReference));
            Parser.Expressions.Reference.MarkNumericSpelled(pc.Expression, name =>
                Array.Exists(table.Columns, peer => peer.SpelledNumeric && collation.Equals(peer.Name, name.Leaf))
                || heapColumns.Exists(peer => peer is { SpelledNumeric: true } && collation.Equals(peer.Name, name.Leaf)));
            heapColumns[pc.Index] = new HeapColumn(pc.Name, computedType, maxLength: null, nullable: pc.Nullable && inferredNullable, computedExpression: pc.Expression, isPersisted: pc.Persisted, computedDefinition: pc.Definition,
                spelledNumeric: computedType is DecimalSqlType && pc.Expression.ResultReportsNumeric);
        }
    }

    /// <summary>
    /// Re-encodes every row in <paramref name="table"/>'s heap against the
    /// post-ADD schema. New columns get NULL (when nullable) or their
    /// DEFAULT-evaluated value (when NOT NULL) or a per-row identity value
    /// (when IDENTITY) — matches SQL Server's probe-confirmed semantic that
    /// DEFAULT on a nullable ADD does NOT backfill, only the NOT NULL form
    /// does, unless the DEFAULT says <c>WITH VALUES</c>
    /// (<paramref name="withValuesColumns"/>). After the rewrite, <paramref name="table"/>'s old <c>Heap</c>
    /// is discarded.
    /// </summary>
    private static void RewriteHeapForAddColumns(HeapTable table, HeapColumn[] newColumns, int existingCount, List<int> withValuesColumns, ParserContext context)
    {
        var anyRows = false;
        foreach (var _ in table.Heap.EnumerateRows())
        {
            anyRows = true;
            break;
        }
        if (!anyRows)
            return;

        // Pre-evaluate the backfilled DEFAULT expressions once (constant snapshot —
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
            // A nullable column takes its DEFAULT only under WITH VALUES.
            if (c.Nullable && !withValuesColumns.Contains(i))
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

        // One encoded-row buffer for the rebuild — Insert copies into the page.
        byte[]? encoded = null;
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
                newStoredValues[newStorageIndex] = c.Identity is not null
                    ? CoerceForIdentity(GenerateIdentity(c), c)
                    : c.Type == SqlType.RowVersion
                        ? SqlValue.FromRowVersion(context.Batch.DatabaseFor(table).AllocateRowVersion())
                        : backfillValues[i] ?? SqlValue.Null(c.Type);
                newStorageIndex++;
            }

            var length = RowEncoder.EncodeRowInto(newStoredColumns, newStoredValues, newHeap, ref encoded);
            _ = newHeap.Insert(encoded.AsSpan(0, length));
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
                if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[i].Name, name))
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

        // Per-column dependency check: one Msg 5074 per blocker, then Msg 4922.
        foreach (var ordinal in toDropOrdinals)
        {
            var col = table.Columns[ordinal];
            var blockers = CollectColumnBlockers(context.Batch.CurrentDatabase, table, ordinal, col, includeCheckAndDefault: true, includeIndexes: true);
            if (blockers.Count > 0)
                throw SimulatedSqlException.ColumnHasDependencies("DROP COLUMN", col.Name, blockers);
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
            // Non-persisted computed columns carry storage ordinal -1
            // (no row-storage slot) — pass those through unmapped.
            for (var i = 0; i < ix.KeyColumns.Length; i++)
            {
                var keyCol = ix.KeyColumns[i];
                var storageOrdinal = keyCol.StorageOrdinal < 0 ? keyCol.StorageOrdinal : oldStorageToNew[keyCol.StorageOrdinal];
                ix.KeyColumns[i] = new IndexKeyColumn(storageOrdinal, oldFullToNew[keyCol.ColumnOrdinal], keyCol.IsDescending);
                // The projected copy the seek path reads moves in lockstep — it
                // is the same ordinal, materialized once at construction.
                ix.KeyStorageOrdinals[i] = storageOrdinal;
            }
            for (var i = 0; i < ix.IncludedColumns.Length; i++)
            {
                if (ix.IncludedColumns[i] >= 0)
                    ix.IncludedColumns[i] = oldStorageToNew[ix.IncludedColumns[i]];
                ix.IncludedColumnOrdinals[i] = oldFullToNew[ix.IncludedColumnOrdinals[i]];
            }
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
    /// Returns true when the given CHECK predicate references a column
    /// by name. Walks the expression tree structurally — same shape the
    /// inline-CHECK peer-reference walker uses at CREATE TABLE.
    /// </summary>
    private static bool CheckPredicateReferencesColumn(Collation collation, BooleanExpression predicate, string columnName)
    {
        var found = false;
        predicate.VisitOperandExpressions(operand =>
        {
            if (found)
                return;
            operand.VisitColumnReferences(reference =>
            {
                if (!found && collation.Equals(reference.Leaf, columnName))
                    found = true;
            });
        });
        return found;
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … ALTER COLUMN col TYPE[(precision[,scale])]
    /// [COLLATE coll] [NULL|NOT NULL]</c>. Cursor on the <c>ALTER</c>
    /// keyword on entry. Single-column shape only (real SQL Server's
    /// grammar doesn't accept comma-separated multi-column ALTER).
    /// </summary>
    /// <remarks>
    /// <para>
    /// COLLATE clause is parse-accepted and ignored — the simulator has
    /// a single default collation. ADD/DROP sub-clause forms
    /// (PERSISTED / MASKED / ROWGUIDCOL / SPARSE) are not modeled
    /// and raise <see cref="NotSupportedException"/>.
    /// </para>
    /// <para>
    /// Apply pipeline matches probe-confirmed SQL Server semantics:
    /// computed and rowversion columns reject with Msg 4928; PK / UQ /
    /// FK (in &amp; out) / computed-column dependencies always block (Msg 5074);
    /// indexes block only on actual <see cref="SqlType"/>-subclass change
    /// (length widening within the same family permitted); existing rows
    /// are coerced per-row through <see cref="SqlValue.CoerceTo"/>, which
    /// raises Msg 245 / 241 / 220 / 8115 for the matching conversion
    /// failures; <c>NULL</c>→<c>NOT NULL</c> flips with existing NULL data
    /// raise Msg 515; identity / DEFAULT / inline CHECK preserve through
    /// the column instance swap.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterTableAlterColumn(ParserContext context, MultiPartName tableName)
    {
        // Cursor on ALTER; advance to COLUMN.
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Column })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columnName = nameToken.Value;

        // `ALTER COLUMN col ADD | DROP <attribute>` follows the column name
        // directly, without a type keyword.
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop } addOrDrop)
            return TryParseAlterColumnAttribute(context, tableName, columnName, adding: addOrDrop.Keyword == Keyword.Add);

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var qualifiedTypeName = BatchContext.ParseObjectName(context);
        var typeName = (Name)context.Token;
        context.MoveNextOptional();

        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);

            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    declaredScale = scaleValue.AsInt32;
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: ')' }:
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }

        // Optional COLLATE clause. The collation name is a single identifier
        // token (e.g. `Latin1_General_BIN`). Captured here and applied to the
        // new SqlType below; when absent, the existing column's collation is
        // preserved.
        string? newCollationName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Collate })
        {
            context.MoveNextRequired();
            newCollationName = Parser.Expressions.CollateExpression.ResolvePseudoCollationName(context.Token switch
            {
                UnquotedString us => us.Value,
                Name n => n.Value,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            }, context.Batch);
            if (!Collation.IsRecognized(newCollationName))
                throw new NotSupportedException($"COLLATE: collation '{newCollationName}' isn't on the simulator's recognized list.");
            context.MoveNextOptional();
        }

        bool? nullable = null;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Not }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                nullable = false;
                context.MoveNextOptional();
                break;
            case ReservedKeyword { Keyword: Keyword.Null }:
                nullable = true;
                context.MoveNextOptional();
                break;
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

        var ordinal = -1;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[i].Name, columnName))
            {
                ordinal = i;
                break;
            }
        }
        if (ordinal < 0)
            throw SimulatedSqlException.AlterColumnDoesNotExist(columnName, table.Name);

        var existingCol = table.Columns[ordinal];
        if (existingCol.Computed is not null)
            throw SimulatedSqlException.CannotAlterColumnOfKind(columnName, "COMPUTED");
        if (existingCol.Type == SqlType.RowVersion)
            throw SimulatedSqlException.CannotAlterColumnOfKind(columnName, "timestamp");
        if (existingCol.GeneratedAs != GeneratedAlwaysAsRow.None)
            throw SimulatedSqlException.PeriodColumnCannotBeAltered(columnName);

        var (newType, newMaxLength, aliasIsNullable, aliasType) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: ordinal + 1, TypeSpecSite.Column, columnName: columnName);
        // For ALTER COLUMN, the precedence is: explicit NULL/NOT NULL on the
        // ALTER clause wins; otherwise alias-default; otherwise preserve
        // existing column nullability. Matches column-on-CREATE-TABLE
        // semantics for the alias-default propagation step.
        var newNullable = nullable ?? aliasIsNullable ?? existingCol.Nullable;

        // Collation: explicit ALTER COLUMN ... COLLATE wins; otherwise
        // preserve the existing column's collation (when the type stays
        // string); when transitioning into a string type from non-string,
        // default to the database default.
        string? newCollationStored;
        if (newType.Category == SqlTypeCategory.String)
        {
            var newCollation =
                (newCollationName is not null ? Collation.TryGet(newCollationName) : null)
                ?? existingCol.Type.Collation
                ?? context.Batch.CurrentDatabase.Collation;
            // See the matching gate in Simulation.Create.cs: text has no
            // per-column collation instance to carry the Msg 459 rejection.
            if (newType is TextSqlType)
                newCollation.RejectIfUnicodeOnly();
            newType = newType.WithCollation(newCollation, Coercibility.Implicit);
            newCollationStored = newCollationName ?? existingCol.Collation;
        }
        else
        {
            newCollationStored = null;
        }

        // Identity preservation: ALTER COLUMN keeps the IdentityState alive
        // (probe-confirmed — INT IDENTITY → BIGINT keeps the counter), and
        // SQL Server forbids changing identity to a non-integer type. The
        // grammar already excludes IDENTITY in the ALTER COLUMN clause (Msg
        // 156 from the parser), so we only need to validate that an existing
        // identity column targets an integer family.
        if (existingCol.Identity is not null && !SqlType.IsIntegerCategory(newType))
            throw SimulatedSqlException.IdentityColumnMustBeIntegerType(columnName);

        // Blocker detection. PK/UQ, FK (both directions), computed-column and
        // schema-bound module dependencies block unconditionally. A CHECK or
        // DEFAULT blocks a change of type — the family, the collation, or a
        // move to or from MAX — but not of length, precision, scale or
        // nullability; an index blocks those and a nullability change too
        // (probed 2026-09-25 against SQL Server 2025: varchar(10)→varchar(5)
        // passes a CHECK, int→bigint and varchar→nvarchar don't).
        var isTypeChange = existingCol.Type.GetType() != newType.GetType()
            || !Equals(existingCol.Type.Collation, newType.Collation)
            || Parser.Expressions.StringScalars.IsMaxForm(existingCol.Type) != Parser.Expressions.StringScalars.IsMaxForm(newType)
            || (existingCol.Type is VarbinarySqlType { length: SqlType.MaxLengthSentinel }) != (newType is VarbinarySqlType { length: SqlType.MaxLengthSentinel });
        var blockers = CollectColumnBlockers(
            context.Batch.CurrentDatabase, table, ordinal, existingCol,
            includeCheckAndDefault: isTypeChange,
            includeIndexes: isTypeChange || newNullable != existingCol.Nullable);
        if (blockers.Count > 0)
            throw SimulatedSqlException.ColumnHasDependencies("ALTER COLUMN", columnName, blockers);

        var newColumn = new HeapColumn(
            existingCol.Name,
            newType,
            newMaxLength,
            newNullable,
            identity: existingCol.Identity,
            defaultExpression: existingCol.Default,
            generatedAs: existingCol.GeneratedAs,
            isHidden: existingCol.IsHidden,
            collation: newCollationStored,
            spelledNumeric: SqlType.IsNumericSpelling(qualifiedTypeName, context.Batch.TryResolveAliasType(qualifiedTypeName, out var spellingAlias) ? spellingAlias : null))
        {
            DefaultConstraint = existingCol.DefaultConstraint,
            AliasType = aliasType,
            // ALTER COLUMN replaces the HeapColumn instance but not the
            // column's catalog identity — real keeps column_id across a type
            // change (probe-confirmed), as it does across sp_rename.
            ColumnId = existingCol.ColumnId,
        };

        // Validate + rewrite. Even when the encoded bytes don't change (e.g.
        // varchar(50)→varchar(100)), the per-column SqlType reference does, so
        // the row decoder needs the new column's Type to be in StoredColumns
        // / Schema before decoding. The strategy: build a candidate new
        // Columns array, swap it in, then do the heap walk under the new
        // schema. If the walk throws, restore the original.
        var originalColumns = table.Columns;
        var newColumns = (HeapColumn[])table.Columns.Clone();
        newColumns[ordinal] = newColumn;
        table.Columns = newColumns;
        table.RecomputeStorageProjections();

        // The rewrite writes every row, so an error it ends with is followed
        // by Msg 3621 as a DML statement's is.
        context.Batch.CurrentStatement.WritesRows = true;
        try
        {
            RewriteHeapForAlterColumn(table, ordinal, newColumn, originalColumns);
        }
        catch
        {
            table.Columns = originalColumns;
            table.RecomputeStorageProjections();
            throw;
        }

        return true;
    }

    /// <summary>
    /// The objects that keep <c>ALTER TABLE { DROP | ALTER } COLUMN</c> from
    /// touching the column at <paramref name="ordinal"/>, in the order real
    /// reports them — by kind (the DEFAULT, computed columns, CHECKs,
    /// schema-bound modules, key constraints, indexes, outgoing then incoming
    /// foreign keys), creation order within a kind (probed 2026-09-25 against
    /// SQL Server 2025). The flags say whether CHECK / DEFAULT constraints and
    /// indexes count; everything else always does.
    /// </summary>
    private static List<(string Name, SimulatedSqlException.AlterColumnBlockerKind Kind)> CollectColumnBlockers(
        Database database, HeapTable table, int ordinal, HeapColumn col, bool includeCheckAndDefault, bool includeIndexes)
    {
        var collation = database.Collation;
        var blockers = new List<(string Name, SimulatedSqlException.AlterColumnBlockerKind Kind, int Rank, int ObjectId)>();
        var storageOrdinal = table.StorageOrdinals[ordinal];
        const SimulatedSqlException.AlterColumnBlockerKind objectKind = SimulatedSqlException.AlterColumnBlockerKind.Object;

        if (includeCheckAndDefault && col.DefaultConstraint is { } df)
            blockers.Add((df.Name, objectKind, 0, df.ObjectId));
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (table.Columns[i].Computed is { } expr && ComputedReferencesColumn(collation, expr, col.Name))
                blockers.Add((table.Columns[i].Name, SimulatedSqlException.AlterColumnBlockerKind.Column, 1, i));
        }
        if (includeCheckAndDefault)
        {
            foreach (var ck in table.CheckConstraints)
            {
                if ((ck.InlineColumn is not null && collation.Equals(ck.InlineColumn, col.Name)) || CheckPredicateReferencesColumn(collation, ck.Predicate, col.Name))
                    blockers.Add((ck.Name, objectKind, 2, ck.ObjectId));
            }
        }
        foreach (var (module, moduleId) in SchemaBinding.ColumnReferencingModules(database, table, col.Name))
            blockers.Add((module, objectKind, 3, moduleId));
        foreach (var kc in table.KeyConstraints)
        {
            if (storageOrdinal >= 0 && Array.IndexOf(kc.StorageOrdinals, storageOrdinal) >= 0)
                blockers.Add((kc.Name, objectKind, 4, kc.ObjectId));
        }
        if (includeIndexes && storageOrdinal >= 0)
        {
            foreach (var ix in table.Indexes)
            {
                if (ix.KeyColumns.Any(k => k.StorageOrdinal == storageOrdinal) || Array.IndexOf(ix.IncludedColumns, storageOrdinal) >= 0)
                    blockers.Add((ix.Name, SimulatedSqlException.AlterColumnBlockerKind.Index, 5, ix.ObjectId));
            }
        }
        foreach (var fk in table.OutgoingForeignKeys)
        {
            if (Array.IndexOf(fk.ChildColumnOrdinals, ordinal) >= 0)
                blockers.Add((fk.Name, objectKind, 6, fk.ObjectId));
        }
        foreach (var fk in table.IncomingForeignKeys)
        {
            if (Array.IndexOf(fk.ReferencedColumnOrdinals, ordinal) >= 0)
                blockers.Add((fk.Name, objectKind, 7, fk.ObjectId));
        }

        return [.. blockers.OrderBy(static b => b.Rank).ThenBy(static b => b.ObjectId).Select(static b => (b.Name, b.Kind))];
    }

    /// <summary>
    /// Returns true when a computed-column expression references the named
    /// column. Structural walk via <see cref="Expression.VisitColumnReferences(Action{MultiPartName})"/>
    /// — same shape <see cref="CheckPredicateReferencesColumn"/> uses.
    /// </summary>
    private static bool ComputedReferencesColumn(Collation collation, Expression computed, string columnName)
    {
        var found = false;
        computed.VisitColumnReferences(reference =>
        {
            if (!found && collation.Equals(reference.Leaf, columnName))
                found = true;
        });
        return found;
    }

    /// <summary>
    /// Walks every row in the heap, decoding the target column under the old
    /// <see cref="HeapColumn"/> layout, coercing to the new type via
    /// <see cref="SqlValue.CoerceTo"/>, and re-encoding against the new
    /// <c>StoredColumns</c> array. Enforces NOT NULL flips by raising Msg 515
    /// on any pre-existing NULL. The conversion call lets <c>CoerceTo</c>'s
    /// own error paths (Msg 220 / 245 / 241 / 8115) surface unchanged;
    /// <see cref="OverflowException"/> from in-range integer narrowing is
    /// translated to Msg 220 with the target type name + offending value.
    /// </summary>
    private static void RewriteHeapForAlterColumn(HeapTable table, int ordinal, HeapColumn newCol, HeapColumn[] originalColumns)
    {
        var oldStorageOrdinal = -1;
        var preAddStoredCount = 0;
        for (var i = 0; i < originalColumns.Length; i++)
        {
            if (!originalColumns[i].IsStored)
                continue;
            if (i == ordinal)
                oldStorageOrdinal = preAddStoredCount;
            preAddStoredCount++;
        }
        // Non-stored column ALTER (computed-without-PERSISTED) can't reach
        // here — TryParseAlterTableAlterColumn rejects computed columns up
        // front with Msg 4928. Assert for clarity; not a reachable runtime.
        if (oldStorageOrdinal < 0)
            return;

        var anyRows = false;
        foreach (var _ in table.Heap.EnumerateRows())
        {
            anyRows = true;
            break;
        }
        if (!anyRows)
            return;

        // Build a snapshot of the pre-ALTER stored-column layout so the
        // decoder reads with the old SqlType references.
        var oldStoredColumns = new HeapColumn[preAddStoredCount];
        var s = 0;
        for (var i = 0; i < originalColumns.Length; i++)
        {
            if (originalColumns[i].IsStored)
                oldStoredColumns[s++] = originalColumns[i];
        }

        var newStoredColumns = table.StoredColumns;
        var newStorageOrdinal = table.StorageOrdinals[ordinal];
        // newStorageOrdinal can't be < 0 since the new column inherits the
        // stored-vs-computed shape from the old (we reject computed columns
        // up front, and IsStored is true for everything else).

        var qualifiedTableName = $"{Database.DefaultSchemaName}.{table.Name}";

        var oldHeap = table.Heap;
        var newHeap = new Heap();
        // One encoded-row buffer for the rebuild — Insert copies into the page.
        byte[]? encoded = null;
        foreach (var oldBytes in oldHeap.EnumerateRows())
        {
            var newStoredValues = new SqlValue[newStoredColumns.Length];
            for (var i = 0; i < oldStoredColumns.Length; i++)
            {
                var decoded = RowDecoder.DecodeColumn(oldStoredColumns, oldBytes, i, oldHeap);
                if (i == oldStorageOrdinal)
                {
                    if (decoded.IsNull)
                    {
                        if (!newCol.Nullable)
                            throw SimulatedSqlException.AlterColumnNullInNonNullColumn(newCol.Name, qualifiedTableName);
                        newStoredValues[newStorageOrdinal] = SqlValue.Null(newCol.Type);
                    }
                    else
                    {
                        SqlValue coerced;
                        try
                        {
                            coerced = decoded.CoerceTo(newCol.Type);
                        }
                        catch (SimulatedSqlException conversion)
                        {
                            conversion.EndedColumnRewrite = true;
                            throw;
                        }
                        catch (OverflowException)
                        {
                            // Same source-type-keyed error family as CAST and
                            // column assignment (probe-confirmed for ALTER
                            // COLUMN too, 2026-07-31): int-family sources give
                            // the value-bearing Msg 220, float/real Msg 232,
                            // a bigint source the generic Msg 8115 naming the
                            // target, and a non-integer narrowing (decimal
                            // precision change) Msg 8115's numeric wording.
                            throw SimulatedSqlException.TryConversionOverflow(decoded, newCol.Type)
                                ?? (SqlType.IsIntegerCategory(decoded.Type)
                                    ? SimulatedSqlException.ArithmeticOverflow(newCol.Type.ToString()!)
                                    : SimulatedSqlException.ArithmeticOverflowToNumeric());
                        }
                        // Bounded-length validation for narrowing varchar /
                        // nvarchar / varbinary. The CoerceTo path itself is
                        // length-agnostic at the SqlValue level — bounded vs
                        // unspecified is a column-level distinction — so the
                        // truncation check lives here.
                        // varchar / char budget N bytes of their collation's
                        // code page; nvarchar budgets N UTF-16 code units.
                        var narrowingEncoding = newCol.Type is VarcharSqlType or CharSqlType
                            ? newCol.Type.Collation!.StorageEncoding
                            : null;
                        if (newCol.MaxLength is int max
                            && max != SqlType.MaxLengthSentinel
                            && (narrowingEncoding?.GetByteCount(coerced.AsString) ?? coerced.AsString.Length) > max)
                        {
                            // Real reports no truncated value for ALTER COLUMN
                            // (probed 2026-09-23).
                            throw SimulatedSqlException.StringOrBinaryWouldBeTruncated(QualifyForTruncationMessage(table), newCol.Name, string.Empty, max, narrowingEncoding);
                        }

                        newStoredValues[newStorageOrdinal] = coerced;
                    }
                }
                else
                {
                    // Map old storage ordinal i to new storage ordinal — the
                    // single-column ALTER doesn't reshuffle other ordinals,
                    // so old i maps to new i for everything except the
                    // altered column.
                    newStoredValues[i] = decoded;
                }
            }

            var length = RowEncoder.EncodeRowInto(newStoredColumns, newStoredValues, newHeap, ref encoded);
            _ = newHeap.Insert(encoded.AsSpan(0, length));
        }

        table.Heap = newHeap;
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
        // One encoded-row buffer for the rebuild — Insert copies into the page.
        byte[]? encoded = null;
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
            var length = RowEncoder.EncodeRowInto(newStoredColumns, newStoredValues, newHeap, ref encoded);
            _ = newHeap.Insert(encoded.AsSpan(0, length));
        }

        table.Heap = newHeap;
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … ALTER COLUMN &lt;col&gt; { ADD | DROP }
    /// { ROWGUIDCOL | SPARSE }</c>. Both attributes are metadata here — the
    /// <c>$ROWGUID</c> pseudo-column isn't modeled and the row encoder already
    /// omits NULLs — so the toggle is a marker flip with no storage effect, and
    /// the catalog is what observes it.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed refusals: a missing column is <b>Msg 4924</b> (State 1,
    /// where the PERSISTED form's is State 2), <c>ADD ROWGUIDCOL</c> on a table
    /// that already has one is <b>Msg 4925</b> and on a non-<c>uniqueidentifier</c>
    /// column <b>Msg 2761</b>, <c>DROP ROWGUIDCOL</c> where nothing carries it is
    /// <b>Msg 4926</b>, and <c>ADD SPARSE</c> on a NOT NULL / IDENTITY /
    /// ROWGUIDCOL column or one of the refused types is <b>Msg 1731</b>, on a
    /// computed column <b>Msg 4928</b> and on one carrying a DEFAULT
    /// <b>Msg 11410</b>. Real follows Msg 4925 / 4926 with a terminating
    /// <b>Msg 1750</b>, which the simulator omits — the first message is the
    /// load-bearing signal.
    /// </remarks>
    private static bool TryParseAlterColumnAttribute(
        ParserContext context, MultiPartName tableName, string columnName, bool adding)
    {
        var attribute = context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.RowGuidCol } => ColumnAttribute.RowGuidCol,
            UnquotedString { ContextualKeyword: ContextualKeyword.Sparse } => ColumnAttribute.Sparse,
            UnquotedString { ContextualKeyword: ContextualKeyword.Persisted } =>
                throw new NotSupportedException("ALTER TABLE ALTER COLUMN ADD / DROP PERSISTED isn't modeled."),
            UnquotedString { ContextualKeyword: ContextualKeyword.Masked } =>
                throw new NotSupportedException("ALTER TABLE ALTER COLUMN ADD / DROP MASKED isn't modeled."),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextOptional();
        // MASKED carries a `WITH (FUNCTION = '…')` clause; the two modeled
        // attributes take nothing.
        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

        HeapColumn? found = null;
        var collation = context.Batch.CurrentDatabase.Collation;
        foreach (var candidate in table.Columns)
        {
            if (collation.Equals(candidate.Name, columnName))
            {
                found = candidate;
                break;
            }
        }
        var target = found ?? throw SimulatedSqlException.AlterColumnDoesNotExist(columnName, table.Name);

        if (attribute == ColumnAttribute.RowGuidCol)
        {
            if (!adding)
            {
                var carrier = FindRowGuidColumn(table) ?? throw SimulatedSqlException.NoRowGuidColToDrop(table.Name);
                carrier.IsRowGuidCol = false;
                return true;
            }
            if (FindRowGuidColumn(table) is not null)
                throw SimulatedSqlException.TableAlreadyHasRowGuidCol(table.Name);
            if (target.Type != SqlType.UniqueIdentifier)
                throw SimulatedSqlException.RowGuidColRequiresUniqueIdentifier();
            target.IsRowGuidCol = true;
            return true;
        }

        if (!adding)
        {
            target.IsSparse = false;
            return true;
        }
        if (target.Computed is not null)
            throw SimulatedSqlException.CannotAlterColumnOfKind(columnName, "COMPUTED");
        if (target.Default is not null)
            throw SimulatedSqlException.CannotMakeColumnSparseWithDefault(columnName, table.Name);
        if (!target.Nullable || target.Identity is not null || target.IsRowGuidCol || !SparseEligible(target.Type))
            throw SimulatedSqlException.CannotCreateSparseColumn(columnName, table.Name);
        target.IsSparse = true;
        return true;
    }

    private enum ColumnAttribute
    {
        RowGuidCol,
        Sparse,
    }

    private static HeapColumn? FindRowGuidColumn(HeapTable table)
    {
        foreach (var column in table.Columns)
        {
            if (column.IsRowGuidCol)
                return column;
        }
        return null;
    }

    /// <summary>
    /// The types real refuses to make sparse: the legacy LOBs, the spatial pair
    /// and user-defined types. Probe-confirmed as part of the Msg 1731 message's
    /// own list.
    /// </summary>
    private static bool SparseEligible(SqlType type) =>
        type != SqlType.Text && type != SqlType.NText && type != SqlType.Image
        && type != SqlType.Geography && type != SqlType.Geometry;
}
