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
            _ = newHeap.Insert(encoded);
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

        // Reject the ADD/DROP sub-clause forms early — `ALTER COLUMN col ADD
        // {PERSISTED|MASKED|…}` and `ALTER COLUMN col DROP {…}` follow the
        // column name directly without a type keyword. The simulator doesn't
        // model these features, so the surface raises NotSupportedException
        // rather than parse-accepting.
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop })
            throw new NotSupportedException("ALTER TABLE ALTER COLUMN sub-clauses (ADD/DROP PERSISTED/MASKED/ROWGUIDCOL/SPARSE) aren't modeled.");

        if (context.Token is not Name typeName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
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

        // Optional COLLATE clause — parse-accept and discard (simulator
        // operates on the default collation only). The collation name is a
        // single identifier token (e.g. `Latin1_General_BIN`).
        if (context.Token is ReservedKeyword { Keyword: Keyword.Collate })
        {
            context.MoveNextRequired();
            if (context.Token is not (Name or UnquotedString))
                throw SimulatedSqlException.SyntaxErrorNear(context);
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
            if (Collation.Default.Equals(table.Columns[i].Name, columnName))
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
            throw new NotSupportedException("ALTER COLUMN on a GENERATED ALWAYS AS ROW START/END column isn't modeled.");

        var (newType, newMaxLength) = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, ordinal + 1, columnName);
        var newNullable = nullable ?? existingCol.Nullable;

        // Identity preservation: ALTER COLUMN keeps the IdentityState alive
        // (probe-confirmed — INT IDENTITY → BIGINT keeps the counter), and
        // SQL Server forbids changing identity to a non-integer type. The
        // grammar already excludes IDENTITY in the ALTER COLUMN clause (Msg
        // 156 from the parser), so we only need to validate that an existing
        // identity column targets an integer family.
        if (existingCol.Identity is not null && !SqlType.IsIntegerCategory(newType))
            throw new NotSupportedException("ALTER COLUMN of an IDENTITY column to a non-integer type isn't modeled.");

        // Blocker detection. PK/UQ, FK (both directions), and computed-column
        // dependencies block unconditionally; indexes block only on actual
        // SqlType-subclass change (varchar(50)→varchar(100) widening passes
        // under an index, varchar→nvarchar doesn't).
        var isSubclassChange = existingCol.Type.GetType() != newType.GetType();
        var blockers = CollectAlterColumnBlockers(table, ordinal, existingCol, isSubclassChange);
        if (blockers.Count > 0)
            throw SimulatedSqlException.AlterColumnHasDependencies(columnName, blockers);

        var newColumn = new HeapColumn(
            existingCol.Name,
            newType,
            newMaxLength,
            newNullable,
            identity: existingCol.Identity,
            defaultExpression: existingCol.Default,
            generatedAs: existingCol.GeneratedAs,
            isHidden: existingCol.IsHidden)
        {
            DefaultConstraint = existingCol.DefaultConstraint,
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
    /// Returns the list of constraints / indexes / computed-column references
    /// that block <c>ALTER COLUMN</c> on the column at
    /// <paramref name="ordinal"/>. PK / UQ / outgoing FK / incoming FK /
    /// computed-column references block unconditionally;
    /// <paramref name="includeIndexes"/> selects whether indexes block (true
    /// when the alteration changes the column's <see cref="SqlType"/>
    /// subclass — probe-confirmed that pure length widening within the same
    /// SqlType family passes under an index).
    /// </summary>
    private static List<(string Name, SimulatedSqlException.AlterColumnBlockerKind Kind)> CollectAlterColumnBlockers(HeapTable table, int ordinal, HeapColumn col, bool includeIndexes)
    {
        var blockers = new List<(string, SimulatedSqlException.AlterColumnBlockerKind)>();
        var storageOrdinal = table.StorageOrdinals[ordinal];

        foreach (var kc in table.KeyConstraints)
        {
            if (storageOrdinal < 0)
                continue;
            foreach (var so in kc.StorageOrdinals)
            {
                if (so == storageOrdinal)
                {
                    blockers.Add((kc.Name, SimulatedSqlException.AlterColumnBlockerKind.Object));
                    break;
                }
            }
        }

        foreach (var fk in table.OutgoingForeignKeys)
        {
            foreach (var co in fk.ChildColumnOrdinals)
            {
                if (co == ordinal)
                {
                    blockers.Add((fk.Name, SimulatedSqlException.AlterColumnBlockerKind.Object));
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
                    blockers.Add((fk.Name, SimulatedSqlException.AlterColumnBlockerKind.Object));
                    break;
                }
            }
        }

        foreach (var c in table.Columns)
        {
            if (c.Computed is { } expr && ComputedReferencesColumn(expr, col.Name))
                blockers.Add((c.Name, SimulatedSqlException.AlterColumnBlockerKind.Column));
        }

        if (includeIndexes)
        {
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
                    blockers.Add((ix.Name, SimulatedSqlException.AlterColumnBlockerKind.Index));
            }
        }

        return blockers;
    }

    /// <summary>
    /// Returns true when a computed-column expression references the named
    /// column. Structural walk via <see cref="Expression.VisitColumnReferences"/>
    /// — same shape <see cref="CheckPredicateReferencesColumn"/> uses.
    /// </summary>
    private static bool ComputedReferencesColumn(Expression computed, string columnName)
    {
        var found = false;
        computed.VisitColumnReferences(reference =>
        {
            if (!found && Collation.Default.Equals(reference.Leaf, columnName))
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
                        catch (OverflowException)
                        {
                            // Msg 220's wording embeds the source's plain
                            // value (e.g. `500` for int → tinyint overflow).
                            // Overflow on a non-integer narrowing (decimal
                            // precision change) routes through Msg 8115.
                            if (!SqlType.IsIntegerCategory(decoded.Type))
                                throw SimulatedSqlException.ArithmeticOverflowToNumeric();
                            // Widen the source value to BigInt so AsInt64
                            // works regardless of which integer subtype the
                            // value carried — int→bigint widening can't
                            // overflow.
                            var formatted = decoded.CoerceTo(SqlType.BigInt).AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            throw SimulatedSqlException.ArithmeticOverflowForDataType(newCol.Type.SqlServerName, formatted);
                        }
                        // Bounded-length validation for narrowing varchar /
                        // nvarchar / varbinary. The CoerceTo path itself is
                        // length-agnostic at the SqlValue level — bounded vs
                        // unspecified is a column-level distinction — so the
                        // truncation check lives here.
                        if (newCol.MaxLength is int max && max != SqlType.MaxLengthSentinel && coerced.AsString.Length > max)
                            throw SimulatedSqlException.StringOrBinaryWouldBeTruncated(table.Name, newCol.Name, coerced.AsString, max);
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

            var encoded = RowEncoder.EncodeRow(newStoredColumns, newStoredValues, newHeap);
            _ = newHeap.Insert(encoded);
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
            _ = newHeap.Insert(encoded);
        }

        table.Heap = newHeap;
    }
}
