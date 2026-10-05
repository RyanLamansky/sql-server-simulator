using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using StoredIndex = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED] INDEX name ON
    /// table (col [ASC | DESC] [, …]) [INCLUDE (col [, …])] [WHERE filter]
    /// [WITH (option [, …])]</c>. Cursor on entry: any of <c>UNIQUE</c> /
    /// <c>CLUSTERED</c> / <c>NONCLUSTERED</c> / <c>INDEX</c> (the keyword
    /// the dispatcher hit just after <c>CREATE</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The simulator has no B-tree storage; non-UNIQUE indexes are catalog
    /// metadata only — they're visible through <c>sys.indexes</c> /
    /// <c>sys.index_columns</c> but don't accelerate queries or constrain
    /// inserts. UNIQUE indexes use the existing key-uniqueness mechanism
    /// (the same NULL-handling rule that <see cref="KeyConstraint"/>
    /// applies — first NULL allowed, second raises Msg 2601). When a
    /// WHERE filter is present, only rows for which the filter evaluates
    /// true participate in the uniqueness check, mirroring SQL Server's
    /// filtered-unique-index semantic.
    /// </para>
    /// <para>
    /// The <c>WITH (option = value, …)</c> clause is parsed and discarded:
    /// <c>FILLFACTOR</c>, <c>PAD_INDEX</c>, <c>IGNORE_DUP_KEY</c>,
    /// <c>ONLINE</c>, etc. are all valid SQL Server options that don't
    /// alter behavior in the simulator. The <c>CLUSTERED</c> keyword is
    /// likewise accepted but doesn't change storage — every table is a
    /// flat heap regardless of declared clustering. It does gate the
    /// <c>INCLUDE</c> list, which real refuses on a clustered index
    /// (Msg 10601) since its leaf already carries every column.
    /// </para>
    /// </remarks>
    internal static bool TryParseCreateIndex(ParserContext context)
    {
        var isUnique = false;
        var isClustered = false;
        while (true)
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Unique } when !isUnique:
                    isUnique = true;
                    context.MoveNextRequired();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Clustered } when !isClustered:
                    isClustered = true;
                    context.MoveNextRequired();
                    continue;
                case ReservedKeyword { Keyword: Keyword.NonClustered }:
                    context.MoveNextRequired();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Index }:
                    break;
                case UnquotedString { Span: var word } when word.Equals("COLUMNSTORE", StringComparison.OrdinalIgnoreCase):
                    return ParseCreateColumnstoreIndex(context, isUnique, isClustered);
                default:
                    return false;
            }
            break;
        }

        // Cursor on INDEX. Index name follows.
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var indexName = nameToken.Value;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var targetTableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var keyColumns = new List<(string Name, bool IsDescending)>();
        do
        {
            if (context.GetNextRequired() is not Name keyCol)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var isDescending = false;
            context.MoveNextRequired();
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Asc }:
                    context.MoveNextRequired();
                    break;
                case ReservedKeyword { Keyword: Keyword.Desc }:
                    isDescending = true;
                    context.MoveNextRequired();
                    break;
            }
            keyColumns.Add((keyCol.Value, isDescending));
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var (includeColumnNames, filter, filterDefinition, indexOptions) = ParseIndexTail(context, indexName, targetTableName.ToString(), acceptsInclude: true, IndexOptionStatement.CreateIndex, refuseFilter: isClustered);
        var ignoreDupKey = indexOptions.IgnoreDupKey;

        // Both statement-shape checks precede every name-resolution error,
        // including a missing table, so they fire here rather than after the
        // target binds — and, being statement-shape checks, regardless of skip
        // state. Probe-confirmed that a clustered INCLUDE reports ahead of
        // IGNORE_DUP_KEY when a statement carries both.
        if (isClustered && includeColumnNames.Count > 0)
            throw SimulatedSqlException.IncludedColumnsOnClusteredIndex();
        if (ignoreDupKey && !isUnique)
            throw SimulatedSqlException.IgnoreDupKeyOnNonUniqueIndex();

        if (context.Batch.IsSkipping)
            return true;

        if (context.Batch.TryResolveSynonym(targetTableName, out _))
            throw SimulatedSqlException.IndexOnNonTableObject(targetTableName.ToString());
        if (!context.Batch.TryResolveTable(targetTableName, out var table))
        {
            // CREATE INDEX ON a view → indexed (materialized) view. Views live
            // in a separate namespace from heap tables, so table resolution
            // misses first; the view path applies the schema-binding /
            // unique-clustered gates and records the index on the View.
            if (context.Batch.TryResolveView(targetTableName, out var view))
            {
                if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(view), view.ObjectId, view.SchemaId))
                    throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());
                if (ignoreDupKey)
                    throw SimulatedSqlException.IgnoreDupKeyOnViewIndex();
                context.Batch.Connection.Simulation.CreateIndexOnView(context, view, indexName, isUnique, isClustered, keyColumns, includeColumnNames, filter, filterDefinition, indexOptions);
                RecordDdlEvent(context, "CREATE_INDEX", EventSchemaName(targetTableName), indexName, "INDEX", view.Name, "VIEW");
                return true;
            }
            // A synonym, function or procedure of that name is no table to index
            // (probed 2026-10-05 against SQL Server 2025).
            if (context.Batch.TryResolveFunctionName(targetTableName, out _) || context.Batch.TryResolveProcedure(targetTableName, out _))
            {
                throw SimulatedSqlException.IndexOnNonTableObject(targetTableName.ToString());
            }
            // A filter binds first, so its table is an ordinary missing object.
            throw filter is not null
                ? SimulatedSqlException.InvalidObjectName(targetTableName, state: 101)
                : SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());
        }

        RejectOnMemoryOptimized(table, "The operation 'CREATE INDEX'", 7);
        RecordTableDdlUndo(context, table);

        // CREATE INDEX is gated on ALTER of the table it lands on — Msg 1088
        // state 12, naming the table as written (probe-confirmed; the DROP /
        // ALTER INDEX forms use state 9).
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());

        var qualifiedTableName = FormatQualifiedTableName(targetTableName, table);
        // A nonclustered index points into the clustered one, which has to be
        // in service (probed 2026-10-05 against SQL Server 2025).
        if (!isClustered && !indexOptions.StatisticsOnly && DisabledClusteredIndexName(table) is not null)
            throw SimulatedSqlException.NonclusteredOnDisabledClustered(indexName, targetTableName.ToString());

        // Unlike Msg 1916, this one names the table, so it can only be raised
        // once the target has bound — probe-confirmed: a filtered index over a
        // missing table reports the missing object instead.
        if (ignoreDupKey && filter is not null)
            throw SimulatedSqlException.IgnoreDupKeyOnFilteredIndex("create", indexName, qualifiedTableName);

        if (filter is not null)
        {
            foreach (var ordinal in BindFilterColumns(context.Batch, table, filter))
            {
                if (table.Columns[ordinal].Type is ClrUdtSqlType or HierarchyIdSqlType or SpatialSqlType)
                    throw SimulatedSqlException.FilteredIndexOnClrColumn(indexName, targetTableName.ToString(), table.Columns[ordinal].Name);
            }
            RejectComputedColumnInIndexFilter(context.Batch, table, indexName, targetTableName.ToString(), filter);
            RejectFilterConstantTypes(context.Batch, table, indexName, targetTableName.ToString(), filter);
        }

        RejectIndexColumnTypes(context.Batch.CurrentDatabase.Collation, table, [.. keyColumns.Select(static k => k.Name)], includeColumnNames, indexName, targetTableName.ToString());
        if (keyColumns.Count > MaxIndexKeyColumns)
            throw SimulatedSqlException.TooManyIndexKeyColumns(indexName, targetTableName.ToString(), keyColumns.Count);
        // A table or view holds at most 999 nonclustered indexes (probed
        // 2026-10-05 against SQL Server 2025).
        if (!isClustered && !indexOptions.DropExisting
            && table.Indexes.Count(static ix => !ix.IsClustered) + table.KeyConstraints.Count(static key => !key.IsClustered) >= 999)
        {
            throw SimulatedSqlException.TooManyNonclusteredIndexes(indexName);
        }

        // DROP_EXISTING = ON replaces the index of that name, keeping its
        // index_id; without it the name must be new (probed 2026-09-26
        // against SQL Server 2025).
        StoredIndex? replaced = null;
        KeyConstraint? replacedConstraint = null;
        foreach (var existing in table.Indexes)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(existing.Name, indexName))
                replaced = indexOptions.DropExisting ? existing : throw SimulatedSqlException.IndexAlreadyExists(indexName, targetTableName.ToString());
        }
        foreach (var kc in table.KeyConstraints)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(kc.Name, indexName))
                replacedConstraint = indexOptions.DropExisting ? kc : throw SimulatedSqlException.IndexAlreadyExists(indexName, targetTableName.ToString());
        }
        // Indexes and statistics share one per-table namespace (probed
        // 2026-10-05 against SQL Server 2025: Msg 1913 names both).
        if (table.UserStatistics.Exists(statistic => context.Batch.CurrentDatabase.Collation.Equals(statistic.Name, indexName))
            || table.JsonIndexes.Exists(json => context.Batch.CurrentDatabase.Collation.Equals(json.Name, indexName))
            || table.VectorIndexes.Exists(vector => context.Batch.CurrentDatabase.Collation.Equals(vector.Name, indexName))
            || table.HypotheticalIndexes.Exists(hypothetical => context.Batch.CurrentDatabase.Collation.Equals(hypothetical.Name, indexName)))
        {
            throw SimulatedSqlException.IndexAlreadyExists(indexName, targetTableName.ToString());
        }
        if (indexOptions.DropExisting)
        {
            if (replaced is null && replacedConstraint is null)
                throw SimulatedSqlException.IndexNotFoundForDropExisting(indexName, targetTableName.ToString());
            if ((replaced?.IsClustered ?? replacedConstraint!.IsClustered) && !isClustered)
                throw SimulatedSqlException.DropExistingClusteredToNonclustered();
        }

        // A table can carry at most one clustered index — a clustered PK/UQ
        // constraint or a prior CREATE CLUSTERED INDEX. Msg 1902 names the
        // existing one (a default PK is clustered).
        if (isClustered && replacedConstraint is null && !indexOptions.StatisticsOnly)
        {
            var existingClustered =
                table.KeyConstraints.FirstOrDefault(k => k.IsClustered)?.Name
                ?? table.Indexes.FirstOrDefault(ix => ix.IsClustered && ix != replaced)?.Name;
            if (existingClustered is not null)
                throw SimulatedSqlException.MoreThanOneClusteredIndex(targetTableName.ToString(), existingClustered);
        }

        RejectDuplicateIndexColumns(context.Batch.CurrentDatabase.Collation, [.. keyColumns.Select(static k => k.Name)], includeColumnNames, inline: false);
        var resolvedKeyColumns = new IndexKeyColumn[keyColumns.Count];
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var fullOrdinal = ResolveColumnOrdinal(context.Batch.CurrentDatabase.Collation, table, keyColumns[i].Name);
            if (table.Columns[fullOrdinal].Type is VectorSqlType or JsonSqlType or ClrUdtSqlType { Udt.IsByteOrdered: false })
                throw SimulatedSqlException.VectorKeyColumnInvalid(table.Columns[fullOrdinal].Name, targetTableName.ToString(), table.Columns[fullOrdinal].Type switch { JsonSqlType => 3, VectorSqlType => 4, _ => 1 });
            // The table is named as the statement wrote it (probed 2026-10-01
            // bare and 2026-10-04 schema-qualified against SQL Server 2025).
            RejectComputedKeyColumnNotIndexable(context.Batch.CurrentDatabase, table.Columns, targetTableName.ToString(), table.Columns[fullOrdinal], indexName, viaConstraint: false);
            resolvedKeyColumns[i] = new IndexKeyColumn(table.StorageOrdinals[fullOrdinal], fullOrdinal, keyColumns[i].IsDescending);
        }
        var resolvedIncludeColumns = new int[includeColumnNames.Count];
        var resolvedIncludeOrdinals = new int[includeColumnNames.Count];
        for (var i = 0; i < includeColumnNames.Count; i++)
        {
            var fullOrdinal = ResolveColumnOrdinal(context.Batch.CurrentDatabase.Collation, table, includeColumnNames[i]);
            // An included computed column is stored in the index, so it has to
            // be deterministic, though it may be imprecise (probed 2026-10-05
            // against SQL Server 2025).
            if (table.Columns[fullOrdinal] is { Computed: not null, IsPersisted: false, ComputedDefinition: { } includedDefinition } included
                && !Schemas.ModuleDeterminism.IsComputedColumnDeterministic(context.CurrentDatabase, table.Columns, includedDefinition))
            {
                throw SimulatedSqlException.ComputedColumnNotDeterministicForIndex(included.Name, targetTableName.ToString(), viaConstraint: false);
            }
            resolvedIncludeColumns[i] = table.StorageOrdinals[fullOrdinal];
            resolvedIncludeOrdinals[i] = fullOrdinal;
        }

        // Recreating a constraint's index keeps the constraint, so the new
        // definition has to be the one it enforces — the same key, unique,
        // unfiltered, nothing included — and only its options change.
        if (replacedConstraint is { } constraint)
        {
            if (!isUnique || filter is not null || resolvedIncludeColumns.Length > 0
                || resolvedKeyColumns.Length != constraint.StorageOrdinals.Length
                || resolvedKeyColumns.Where((k, i) => k.StorageOrdinal != constraint.StorageOrdinals[i] || k.IsDescending != constraint.IsDescending(i)).Any())
            {
                throw SimulatedSqlException.DropExistingConstraintMismatch(constraint.Name);
            }
            constraint.FillFactor = indexOptions.FillFactor ?? 0;
            constraint.IsPadded = indexOptions.PadIndex ?? false;
            constraint.DataCompression = indexOptions.DataCompression ?? 0;
            constraint.XmlCompression = indexOptions.XmlCompression ?? false;
            BuildStatistics(context.Batch, table, statistic => ReferenceEquals(statistic.State, constraint.Statistics));
            RecordDdlEvent(context, "CREATE_INDEX", EventSchemaName(targetTableName), indexName, "INDEX", table.Name, "TABLE");
            return true;
        }

        var index = new StoredIndex(
            indexName,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            isUnique,
            isClustered,
            resolvedKeyColumns,
            resolvedIncludeColumns,
            resolvedIncludeOrdinals,
            filter,
            filterDefinition,
            indexOptions);

        // A filtered index or one over a computed column stores the value of
        // an expression, so real refuses to build it from a session whose SET
        // options would read that expression differently (Msg 1934, naming
        // every offending option). A plain index over plain columns is
        // unaffected (probe-confirmed).
        // A table created under ANSI_NULLS OFF carries that capture, which
        // refuses such an index first (Msg 1935, probed 2026-10-04 against SQL
        // Server 2025).
        if ((filter is not null || IndexCoversComputedColumn(table, index)) && !table.UsesAnsiNulls)
            throw SimulatedSqlException.ObjectCreatedWithSetOptionsOff(table.Name, "ANSI_NULLS");
        if ((filter is not null || IndexCoversComputedColumn(table, index)) && IncorrectSetOptionNames(context) is { } setOptions)
            throw SimulatedSqlException.IncorrectSetOptions("CREATE INDEX", setOptions);

        // A hypothetical index is never built: no uniqueness check, no
        // placement, no storage — a catalog entry and a statistic (probed
        // 2026-10-02 against SQL Server 2025: a UNIQUE one admits duplicates).
        if (index.IsHypothetical)
        {
            table.SettleIndexIds();
            table.HypotheticalIndexes.Add(index);
            table.NoteStatisticsCreated(index.Name, context.CurrentDatabase.Collation);
            RecordDdlEvent(context, "CREATE_INDEX", EventSchemaName(targetTableName), indexName, "INDEX", table.Name, "TABLE");
            return true;
        }

        index.KeyMayExceedLimit = WarnOfWideIndexKey(context.Batch, table.Columns, [.. resolvedKeyColumns.Select(static key => key.ColumnOrdinal)], indexName, isClustered, rejectFixedOverflow: true);
        if (index.KeyMayExceedLimit)
        {
            table.KeysMayExceedLimit = true;
            SqlValue[]? existing = null;
            foreach (var rowBytes in table.Heap.EnumerateRows())
                RejectOversizedEntry(index.Name, index.KeyFullOrdinals, isClustered, DecodeFullRowWithComputed(table, rowBytes, context.Batch, ref existing));
        }
        var placement = PlacementFor(context.Batch, table, index.WrittenDataSpace);
        var filegroup = FilegroupFor(context.Batch, table, index.WrittenDataSpace);
        // Partition-level options need a partitioned index (probed 2026-10-05
        // against SQL Server 2025).
        if (placement is null && indexOptions.CompressionOnPartitions)
            throw SimulatedSqlException.PartitionNumberOnUnpartitionedCreate(indexName);
        if (placement is not null && indexOptions.PartitionCompressions is { } compressionClauses)
        {
            index.PartitionDataCompression = PartitionCompression.Apply(index.DataCompression, null, placement.Fanout, compressionClauses,
                number => SimulatedSqlException.InvalidPartitionNumber(number, indexName, placement.Fanout, kind: "index"));
        }
        if (placement is null && indexOptions.StatisticsIncremental)
            throw SimulatedSqlException.StatisticsCannotBeIncremental(state: 9);
        if (IndexCoversComputedColumn(table, index))
            EvaluateIndexedComputedColumns(table, index, context.Batch);
        if (isUnique)
        {
            RequirePartitionColumnInUniqueKey(placement, table, [.. resolvedKeyColumns.Select(static key => key.ColumnOrdinal)], indexName, isConstraint: false);
            ValidateExistingRowsForUniqueIndex(table, index, context.Batch, qualifiedTableName);
        }
        if (placement is null)
            RejectIndexOnEmptyFilegroup(context.Batch, table, filegroup);

        if (replaced is not null)
        {
            index.IndexId = replaced.IndexId;
            table.Indexes[table.Indexes.IndexOf(replaced)] = index;
        }
        else
        {
            table.SettleIndexIds();
            table.Indexes.Add(index);
        }
        table.NoteStatisticsCreated(index.Name, context.CurrentDatabase.Collation);
        // Building an index builds its statistic; a clustered one rebuilds
        // every index, whose rows now point into it.
        BuildStatistics(context.Batch, table, statistic => isClustered ? statistic.User is null : ReferenceEquals(statistic.State, index.Statistics));
        if (isClustered)
        {
            table.Partitioning = placement;
            table.FilegroupId = filegroup;
        }
        else
        {
            index.Partitioning = placement;
            index.FilegroupId = filegroup;
        }
        RecordDdlEvent(context, "CREATE_INDEX", EventSchemaName(targetTableName), indexName, "INDEX", table.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// Raises <b>Msg 10609</b> when a filtered index's predicate reads a
    /// computed column. Real refuses it whether or not the column is
    /// <c>PERSISTED</c>: deciding a row's membership means evaluating the
    /// predicate, and real won't key an index's contents on an expression it
    /// re-derives. The simulator accepting one was the more dangerous
    /// divergence direction — its filter evaluation reads the column out of a
    /// decoded row, where a non-persisted computed slot is NULL, so every such
    /// row silently fell outside the filter.
    /// </summary>
    private static void RejectComputedColumnInIndexFilter(
        BatchContext batch, HeapTable table, string indexName, string qualifiedTableName, BooleanExpression filter, bool forStatistics = false)
    {
        var collation = batch.CurrentDatabase.Collation;
        string? offending = null;
        filter.VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
        {
            if (offending is not null)
                return;
            foreach (var column in table.Columns)
            {
                if (collation.Equals(column.Name, name.Leaf) && column.Computed is not null)
                {
                    offending = column.Name;
                    return;
                }
            }
        }));

        if (offending is not null)
            throw SimulatedSqlException.FilteredIndexOnComputedColumn(indexName, qualifiedTableName, offending, forStatistics);
    }

    /// <summary>
    /// Refuses a filter comparing a CLR-typed column (Msg 10619) or comparing a
    /// column with a constant the column would have to be converted to — an
    /// approximate number against an exact column, a non-string against a
    /// string column, a Unicode string against a non-Unicode one (Msg 10611);
    /// an exact number of higher precedence, money, a binary or a date string
    /// is converted to the column instead (probed 2026-10-05 against SQL Server
    /// 2025).
    /// </summary>
    private static void RejectFilterConstantTypes(BatchContext batch, HeapTable table, string indexName, string writtenTableName, BooleanExpression filter)
    {
        var collation = batch.CurrentDatabase.Collation;
        filter.VisitFilterComparisons((name, constant) =>
        {
            if (Array.Find(table.Columns, column => collation.Equals(column.Name, name.Leaf)) is not { } column)
                return;
            if (column.Type is ClrUdtSqlType or HierarchyIdSqlType or SpatialSqlType)
                throw SimulatedSqlException.FilteredIndexOnClrColumn(indexName, writtenTableName, column.Name);
            SqlType constantType;
            try
            {
                constantType = constant.GetSqlType(batch, static name => throw SimulatedSqlException.InvalidColumnName(name));
            }
            catch (SimulatedSqlException)
            {
                return;
            }
            var columnType = column.Type;
            var columnIsString = columnType.Category == SqlTypeCategory.String;
            var refused = (constantType.Category == SqlTypeCategory.Approximate && columnType.Category != SqlTypeCategory.Approximate)
                || (columnIsString && constantType.Category != SqlTypeCategory.String && constantType is not (VarbinarySqlType or BinarySqlType))
                || (columnType is VarcharSqlType or CharSqlType && constantType is NVarcharSqlType or NCharSqlType);
            if (refused)
                throw SimulatedSqlException.FilterConstantOfHigherPrecedence(indexName, writtenTableName, column.Name);
        });
    }

    /// <summary>
    /// Builds the indexes declared inline in a CREATE TABLE, a table variable
    /// or a table type's instance (the table-level <c>INDEX ix (cols)</c> and
    /// column-level <c>col type INDEX ix</c> forms) against the freshly-created
    /// <paramref name="table"/>. Each maps to the
    /// same <see cref="StoredIndex"/> the standalone CREATE INDEX builds
    /// (catalog metadata + seek acceleration), <c>UNIQUE</c>, <c>INCLUDE</c>, a
    /// filter and <c>IGNORE_DUP_KEY</c> included; the table is empty, so there
    /// are no existing rows for a unique one to check.
    /// </summary>
    internal static void AddInlineIndexes(BatchContext batch, HeapTable table, string writtenTableName, IReadOnlyList<PendingInlineIndex> pendingIndexes, int[]? objectIds = null)
    {
        var collation = batch.CurrentDatabase.Collation;
        for (var position = 0; position < pendingIndexes.Count; position++)
        {
            var pending = pendingIndexes[position];
            foreach (var existing in table.Indexes)
            {
                if (collation.Equals(existing.Name, pending.Name))
                    throw SimulatedSqlException.IndexAlreadyExists(pending.Name, writtenTableName);
            }
            foreach (var kc in table.KeyConstraints)
            {
                if (collation.Equals(kc.Name, pending.Name))
                    throw SimulatedSqlException.IndexAlreadyExists(pending.Name, writtenTableName);
            }
            if (pending.IsClustered)
            {
                var existingClustered =
                    table.KeyConstraints.FirstOrDefault(k => k.IsClustered)?.Name
                    ?? table.Indexes.FirstOrDefault(ix => ix.IsClustered)?.Name;
                if (existingClustered is not null)
                    throw SimulatedSqlException.MoreThanOneClusteredIndex(table.Name, existingClustered);
            }
            if (pending.Filter is not null)
                RejectComputedColumnInIndexFilter(batch, table, pending.Name, writtenTableName, pending.Filter);

            if (pending.IsColumnstore)
            {
                table.Indexes.Add(ResolveInlineColumnstoreIndex(batch, table, pending, objectIds?[position] ?? batch.CurrentDatabase.AllocateObjectId()));
                continue;
            }

            RejectDuplicateIndexColumns(collation, [.. pending.Columns.Select(static c => c.ColumnName)], pending.IncludeColumnNames, inline: true);
            // CREATE TABLE's own index follows the refusal with Msg 1750, at
            // state 0 after a key's 1919 and state 1 after an included column's
            // 1999 (probed 2026-09-30 against SQL Server 2025).
            try
            {
                RejectIndexColumnTypes(collation, table, [.. pending.Columns.Select(static c => c.ColumnName)], pending.IncludeColumnNames, pending.Name, writtenTableName, inline: true);
            }
            catch (SimulatedSqlException refused) when (refused.Number is 1919 or 1999)
            {
                throw SimulatedSqlException.FollowedByConstraintNotCreated(refused, state: refused.Number == 1919 ? (byte)0 : (byte)1);
            }

            var keyColumns = new IndexKeyColumn[pending.Columns.Length];
            for (var i = 0; i < pending.Columns.Length; i++)
            {
                var fullOrdinal = ResolveColumnOrdinal(collation, table, pending.Columns[i].ColumnName);
                keyColumns[i] = new IndexKeyColumn(table.StorageOrdinals[fullOrdinal], fullOrdinal, pending.Columns[i].IsDescending);
            }
            var includeColumns = new int[pending.IncludeColumnNames.Count];
            var includeOrdinals = new int[pending.IncludeColumnNames.Count];
            for (var i = 0; i < includeColumns.Length; i++)
            {
                var fullOrdinal = ResolveColumnOrdinal(collation, table, pending.IncludeColumnNames[i]);
                includeColumns[i] = table.StorageOrdinals[fullOrdinal];
                includeOrdinals[i] = fullOrdinal;
            }
            var index = new StoredIndex(
                pending.Name,
                objectIds?[position] ?? batch.CurrentDatabase.AllocateObjectId(),
                pending.IsUnique,
                pending.IsClustered,
                keyColumns,
                includeColumns,
                includeOrdinals,
                pending.Filter,
                pending.FilterDefinition,
                pending.Options);
            if (!table.IsTableVariable && !table.IsTypeTable)
                PlaceNewIndex(batch, table, index);
            table.Indexes.Add(index);
            table.NoteStatisticsCreated(index.Name, batch.CurrentDatabase.Collation);
        }
    }

    /// <summary>
    /// A declaration's columnstore index, checked as a standalone one is:
    /// one per table, and only columns its kind can hold.
    /// </summary>
    private static StoredIndex ResolveInlineColumnstoreIndex(BatchContext batch, HeapTable table, PendingInlineIndex pending, int objectId)
    {
        var collation = batch.CurrentDatabase.Collation;
        if (table.Indexes.Exists(ix => ix.IsColumnstore))
            throw SimulatedSqlException.MultipleColumnstoreIndexes();
        RejectDuplicateIndexColumns(collation, [.. pending.Columns.Select(static c => c.ColumnName)], [], inline: true);
        var ordinals = pending.IsClustered
            ? [.. Enumerable.Range(0, table.Columns.Length)]
            : pending.Columns.Select(c => ResolveColumnOrdinal(collation, table, c.ColumnName)).ToArray();
        foreach (var ordinal in ordinals)
        {
            if (!pending.IsClustered && table.Columns[ordinal].Computed is not null)
                throw SimulatedSqlException.ColumnstoreIndexComputedColumn(table.Columns[ordinal].Name, table.Name);
            if (ColumnstoreRefusesType(table.Columns[ordinal], pending.IsClustered))
                throw SimulatedSqlException.ColumnstoreUnsupportedType(table.Columns[ordinal].Name);
        }
        var fullOrdinals = pending.IsClustered ? [] : ordinals;
        return new StoredIndex(
            pending.Name,
            objectId,
            isUnique: false,
            pending.IsClustered,
            keyColumns: [],
            [.. fullOrdinals.Select(o => table.StorageOrdinals[o])],
            fullOrdinals,
            pending.Filter,
            pending.FilterDefinition,
            pending.Options,
            isColumnstore: true,
            columnstoreOrder: [.. pending.ColumnstoreOrder.Select(name => ResolveColumnOrdinal(collation, table, name))]);
    }

    /// <summary>
    /// Refuses an index naming a column twice (Msg 1909), in its key list or
    /// between that and its <c>INCLUDE</c> list; an index declared inline in
    /// <c>CREATE TABLE</c> follows it with Msg 1750 state 0.
    /// </summary>
    private static void RejectDuplicateIndexColumns(Collation collation, string[] keyColumnNames, List<string> includeColumnNames, bool inline)
    {
        string[] all = [.. keyColumnNames, .. includeColumnNames];
        for (var i = 1; i < all.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (!collation.Equals(all[i], all[j]))
                    continue;
                var duplicate = SimulatedSqlException.DuplicateIndexColumn(all[i], state: i < keyColumnNames.Length ? (byte)1 : (byte)2);
                throw inline ? SimulatedSqlException.FollowedByConstraintNotCreated(duplicate, state: 0) : duplicate;
            }
        }
    }

    /// <summary>
    /// Refuses a key or included column whose type an index can't hold, ahead
    /// of the name, the clustered-index and every later check: a LOB key is
    /// Msg 1919 naming the table as written, an <c>xml</c> key Msg 1977, a
    /// spatial key Msg 1978, and a <c>text</c> / <c>ntext</c> / <c>image</c>
    /// included column Msg 1999 (probed 2026-09-30 against SQL Server 2025:
    /// an index named like an existing one over an <c>nvarchar(max)</c> key is
    /// Msg 1919, not Msg 1913). An index CREATE TABLE declares
    /// (<paramref name="inline"/>) reports an <c>xml</c> or spatial key as Msg
    /// 1919 too. A name no column answers is left to the resolution that
    /// follows.
    /// </summary>
    private static void RejectIndexColumnTypes(Collation collation, HeapTable table, List<string> keyColumnNames, List<string> includeColumnNames, string indexName, string writtenTableName, bool inline = false)
    {
        foreach (var name in keyColumnNames)
        {
            switch (Array.Find(table.Columns, column => collation.Equals(column.Name, name)))
            {
                case { Type: XmlSqlType or SpatialSqlType } typed when inline:
                    throw SimulatedSqlException.KeyColumnInvalidType(typed.Name, writtenTableName);
                case { Type: XmlSqlType } xml:
                    throw SimulatedSqlException.IndexKeyOnXmlColumn(indexName, writtenTableName, xml.Name);
                case { Type: SpatialSqlType } spatial:
                    throw SimulatedSqlException.VectorKeyColumnInvalid(spatial.Name, writtenTableName, state: 1);
                case { Type: not (VectorSqlType or JsonSqlType), IsLob: true } lob:
                    throw SimulatedSqlException.KeyColumnInvalidType(lob.Name, writtenTableName);
                // A computed column's MAX type rides on its type alone.
                case { Computed: not null, Type: VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel } or VarbinarySqlType { length: SqlType.MaxLengthSentinel } } computedMax:
                    throw SimulatedSqlException.KeyColumnInvalidType(computedMax.Name, writtenTableName);
            }
        }
        foreach (var name in includeColumnNames)
        {
            if (Array.Find(table.Columns, column => collation.Equals(column.Name, name)) is { Type.IsLegacyLob: true } legacy)
                throw SimulatedSqlException.IncludedColumnInvalidType(legacy.Name, writtenTableName);
        }
    }

    private static int ResolveColumnOrdinal(Collation collation, HeapTable table, string columnName)
    {
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (collation.Equals(table.Columns[i].Name, columnName))
                return i;
        }
        // A node's or edge's identifier keys an index as the graph id it
        // renders (probed 2026-10-05 against SQL Server 2025); the endpoint
        // pseudo-columns key as the column they name.
        return GraphColumns.IdentifierKeyOrdinal(table.Columns, columnName) is var graphId and >= 0 ? graphId
            : columnName.StartsWith('$') && Array.FindIndex(table.Columns, c => GraphColumns.IsPseudoColumnFor(c.Name, columnName)) is var pseudo and >= 0 ? pseudo
            : throw SimulatedSqlException.IndexColumnMissing(columnName);
    }

    /// <summary>
    /// Schema-qualified table name in the form <c>dbo.Table</c> — used in
    /// index-related error messages where SQL Server's wording always
    /// includes the schema (verbatim against probed Msg 1913 / 3701 / 3723).
    /// </summary>
    private static string FormatQualifiedTableName(MultiPartName written, HeapTable table) =>
        written.Count >= 2 ? $"{written.ImmediateQualifier}.{table.Name}" : $"{Database.DefaultSchemaName}.{table.Name}";

    /// <summary>
    /// Linear-scan validation of existing rows for a new UNIQUE index.
    /// Decodes each row's key tuple (and evaluates the WHERE filter when
    /// present, skipping rows whose filter doesn't evaluate true), raising
    /// Msg 1505 on the first duplicate. Filter-aware: rows excluded by the
    /// filter aren't checked, mirroring SQL Server's filtered-unique-index
    /// semantic.
    /// </summary>
    private static void ValidateExistingRowsForUniqueIndex(HeapTable table, StoredIndex index, BatchContext batch, string qualifiedTableName)
    {
        // Hashed rather than compared against every prior key: the walk this
        // replaces was quadratic in the table's row count, which a computed key
        // (whose every key read evaluates an expression) makes twice as costly.
        var seen = new HashSet<SqlValueKey>();
        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        // A filter, or a key naming a non-persisted computed column, needs the
        // whole row — the latter because the value exists nowhere else.
        var needsFullRow = index.Filter is not null || !index.KeysAreStored;
        SqlValue[]? fullRow = null;
        SqlValue[]? lowestDuplicate = null;

        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            if (needsFullRow)
            {
                fullRow = DecodeFullRowWithComputed(table, rowBytes, batch, ref fullRow);
                if (index.Filter is { } filter && EvaluateIndexFilter(filter, table, fullRow, batch) != true)
                    continue;
            }

            SqlValue[] key;
            if (index.KeysAreStored)
            {
                key = new SqlValue[index.KeyColumns.Length];
                for (var k = 0; k < index.KeyColumns.Length; k++)
                    key[k] = RowDecoder.DecodeColumn(storedColumns, rowBytes, index.KeyColumns[k].StorageOrdinal, lobStore);
            }
            else
            {
                key = ReadKeyByFullOrdinals(index.KeyFullOrdinals, fullRow!);
            }

            if (!seen.Add(new SqlValueKey(key)) && (lowestDuplicate is null || CompareIndexKeys(key, lowestDuplicate, index.KeyColumns) < 0))
                lowestDuplicate = key;
        }
        if (lowestDuplicate is not null)
            throw SimulatedSqlException.DuplicateKeyOnCreate(qualifiedTableName, index.Name, FormatIndexKeyValues(lowestDuplicate));
    }

    /// <summary>
    /// Orders two key tuples as the index orders them — each column ascending or
    /// descending as declared, a NULL lowest — which is the order real's sort
    /// meets duplicates in when it builds a unique index, so the duplicate a
    /// Msg 1505 quotes is the first in key order, not in write order (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static int CompareIndexKeys(SqlValue[] x, SqlValue[] y, IndexKeyColumn[] columns) =>
        CompareIndexKeys(x, y, i => i < columns.Length && columns[i].IsDescending);

    /// <inheritdoc cref="CompareIndexKeys(SqlValue[], SqlValue[], IndexKeyColumn[])"/>
    internal static int CompareIndexKeys(SqlValue[] x, SqlValue[] y, Func<int, bool> isDescending)
    {
        for (var i = 0; i < x.Length; i++)
        {
            int c;
            if (x[i].IsNull || y[i].IsNull)
                c = x[i].IsNull == y[i].IsNull ? 0 : x[i].IsNull ? -1 : 1;
            else
                c = x[i].CompareTo(y[i]);
            if (c != 0)
                return isDescending(i) ? -c : c;
        }
        return 0;
    }

    /// <summary>
    /// Evaluates a filtered-index <c>WHERE</c> predicate against a single
    /// row. <paramref name="rowValues"/> is indexed in full-column order
    /// (matching <see cref="HeapTable.Columns"/>); the resolver maps a
    /// referenced column name to its slot via case-insensitive name
    /// compare, the same shape <c>EnforceCheckConstraints</c> uses.
    /// </summary>
    internal static bool? EvaluateIndexFilter(BooleanExpression filter, HeapTable table, SqlValue[] rowValues, BatchContext batch)
    {
        SqlValue ResolveByName(MultiPartName reference)
        {
            for (var k = 0; k < table.Columns.Length; k++)
            {
                if (batch.CurrentDatabase.Collation.Equals(table.Columns[k].Name, reference.Leaf))
                    return rowValues[k];
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }
        var runtime = new RuntimeContext(ResolveByName, batch);
        return filter.Run(runtime);
    }

    /// <summary>
    /// Renders an index-violation key tuple for Msg 2601 the same way
    /// <c>FormatKeyValue</c> does for Msg 2627 (NULL as <c>&lt;NULL&gt;</c>,
    /// strings raw, numerics in invariant culture). Reuses the existing
    /// helper.
    /// </summary>
    internal static string FormatIndexKeyValues(SqlValue[] keyValues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < keyValues.Length; i++)
        {
            if (i > 0)
                _ = sb.Append(", ");
            _ = sb.Append(FormatKeyValue(keyValues[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The clauses an index takes after its key list, standalone or declared
    /// inline in a <c>CREATE TABLE</c>: <c>INCLUDE (…)</c> where
    /// <paramref name="acceptsInclude"/> says (a column-level inline index
    /// takes none), a <c>WHERE</c> filter, <c>WITH (option = value, …)</c> and
    /// an <c>ON</c> placement, which rides back on
    /// <see cref="IndexOptions.DataSpace"/>. Every index option but
    /// <c>IGNORE_DUP_KEY</c>, the one with a semantic here, is discarded.
    /// </summary>
    private static (List<string> IncludeColumnNames, BooleanExpression? Filter, string? FilterDefinition, IndexOptions Options) ParseIndexTail(
        ParserContext context, string indexName, string tableLeaf, bool acceptsInclude, IndexOptionStatement statement = IndexOptionStatement.Unchecked, string? optionIndexName = null, bool refuseFilter = false)
    {
        var includeColumnNames = new List<string>();
        if (acceptsInclude && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Include })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            do
            {
                if (context.GetNextRequired() is not Name incCol)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                includeColumnNames.Add(incCol.Value);
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }

        BooleanExpression? filter = null;
        string? filterDefinition = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            // A clustered index takes no filter: the parser stops at WHERE
            // (probed 2026-10-05 against SQL Server 2025).
            if (refuseFilter)
                throw SimulatedSqlException.SyntaxErrorNearText("WHERE");
            context.MoveNextRequired();
            RejectFilterPredicateKeywords(context);
            filter = BooleanExpression.Parse(context);
            CheckFilterPredicate(filter, statistics: false, indexName, tableLeaf);
            // Render the parsed predicate into SQL Server's normalized
            // filter_definition form ([col]=(1) AND …) for sys.indexes. Null
            // when the predicate falls outside the renderable filtered grammar
            // — exactly the shapes a real server rejects in a filtered index.
            filterDefinition = filter.RenderFilterDefinition(context.Batch);
        }

        var options = ParseOptionalIndexWithClause(context, statement, optionIndexName)
            .WithDataSpace(ParseOptionalDataSpaceClause(context, out _));
        // An index's FILESTREAM_ON names where FILESTREAM data goes, which no
        // table here has (Msg 1716 state 2, probed 2026-10-05).
        if (statement == IndexOptionStatement.CreateIndex && context.Token is StringToken { Span: var fileStreamKeyword }
            && fileStreamKeyword.Equals("FILESTREAM_ON", StringComparison.OrdinalIgnoreCase))
        {
            if (context.GetNextRequired() is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            if (!context.Batch.IsSkipping)
                throw SimulatedSqlException.FileStreamOnWithoutFileStreamColumns(state: 2);
        }
        return (includeColumnNames, filter, filterDefinition, options);
    }

    /// <summary>
    /// The refusals a filtered index's or statistic's parsed <c>WHERE</c>
    /// meets, in the order real reports them: a variable (Msg 112), a
    /// subquery (Msg 1046), a comparison with a literal <c>NULL</c>
    /// (Msg 10620), then anything but an AND of column-against-constant
    /// comparisons (Msg 10735); probed 2026-09-30 against SQL Server 2025.
    /// </summary>
    internal static void CheckFilterPredicate(BooleanExpression filter, bool statistics, string objectName, string tableLeaf)
    {
        var hasVariable = false;
        var hasSubquery = false;
        filter.VisitOperandExpressions(operand => operand.Walk((node, _) =>
        {
            hasVariable |= node is VariableReference;
            hasSubquery |= node is ScalarSubqueryExpression;
            return true;
        }));
        if (hasVariable)
            throw SimulatedSqlException.VariablesNotAllowedInCreateIndex();
        if (hasSubquery)
            throw SimulatedSqlException.SubqueriesNotAllowedInThisContext();
        if (filter.FilterComparesToNullLiteral)
            throw SimulatedSqlException.FilterComparesToNullLiteral(statistics, objectName, tableLeaf);
        if (!filter.IsFilteredIndexShape)
        {
            throw statistics
                ? SimulatedSqlException.IncorrectFilteredStatisticsWhereClause(objectName, tableLeaf)
                : SimulatedSqlException.IncorrectFilteredIndexWhereClause(objectName, tableLeaf);
        }
    }

    /// <summary>
    /// The <see cref="HeapTable.Columns"/> indices a filter reads; a name the
    /// table lacks is Msg 207, which real reports ahead of the key columns'
    /// own Msg 1911.
    /// </summary>
    internal static int[] BindFilterColumns(BatchContext batch, HeapTable table, BooleanExpression filter)
    {
        var collation = batch.CurrentDatabase.Collation;
        List<int> ordinals = [];
        MultiPartName? missing = null;
        filter.VisitOperandExpressions(operand => operand.VisitColumnReferences(reference =>
        {
            var found = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, reference.Leaf));
            if (found < 0)
                missing ??= reference;
            else if (!ordinals.Contains(found))
                ordinals.Add(found);
        }));
        return missing is { } unresolved ? throw SimulatedSqlException.InvalidColumnName(unresolved) : [.. ordinals];
    }

    /// <summary>
    /// A filtered index's WHERE takes only an AND of comparisons, so real
    /// refuses the other connectives as the parser meets them — Msg 156 at
    /// <c>OR</c>, <c>LIKE</c>, <c>BETWEEN</c>, <c>EXISTS</c> or a leading
    /// <c>NOT</c>, and Msg 102 near <c>NOT</c> for <c>NOT IN</c> (probed
    /// 2026-09-24 against SQL Server 2025). Scans ahead and restores the
    /// cursor, leaving the ordinary parse to build the predicate.
    /// </summary>
    private static void RejectFilterPredicateKeywords(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var depth = 0;
        var expectTerm = true;
        Token? previous = null;
        while (context.Token is { } token)
        {
            switch (token)
            {
                case ReservedKeyword { Keyword: Keyword.With or Keyword.On } when depth == 0:
                case Operator { Character: ';' }:
                    context.RestoreCheckpoint(checkpoint);
                    return;
                case ReservedKeyword { Keyword: Keyword.Or or Keyword.Like or Keyword.Between or Keyword.Exists } keyword:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);
                case ReservedKeyword { Keyword: Keyword.Not } keyword when previous is not ReservedKeyword { Keyword: Keyword.Is }:
                    throw expectTerm ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword) : SimulatedSqlException.SyntaxErrorNearText("NOT");
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
            }
            expectTerm = token is Operator { Character: '(' } or ReservedKeyword { Keyword: Keyword.And };
            previous = token;
            if (!context.MoveNext())
                break;
        }
        context.RestoreCheckpoint(checkpoint);
    }
}
