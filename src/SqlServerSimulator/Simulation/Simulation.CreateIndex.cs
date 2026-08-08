using System.Text;
using SqlServerSimulator.Parser;
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

        var includeColumnNames = new List<string>();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Include })
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
            context.MoveNextRequired();
            filter = BooleanExpression.Parse(context);
            // Render the parsed predicate into SQL Server's normalized
            // filter_definition form ([col]=(1) AND …) for sys.indexes. Null
            // when the predicate falls outside the renderable filtered grammar
            // — exactly the shapes a real server rejects in a filtered index.
            filterDefinition = filter.RenderFilterDefinition(context.Batch);
        }

        // WITH (option = value, …) followed by an optional ON <filegroup>.
        // The filegroup clause is discarded (no filegroup model) and so is every
        // index option except IGNORE_DUP_KEY, the one with a semantic here.
        var ignoreDupKey = ParseOptionalIndexWithClause(context);
        SkipOptionalFilegroupClause(context);

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
                context.Batch.Connection.Simulation.CreateIndexOnView(context, view, indexName, isUnique, isClustered, keyColumns, includeColumnNames, filter, filterDefinition);
                RecordDdlEvent(context, "CREATE_INDEX", EventSchemaName(targetTableName), indexName, "INDEX", view.Name, "VIEW");
                return true;
            }
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());
        }

        // CREATE INDEX is gated on ALTER of the table it lands on — Msg 1088
        // state 12, naming the table as written (probe-confirmed; the DROP /
        // ALTER INDEX forms use state 9).
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());

        var qualifiedTableName = FormatQualifiedTableName(targetTableName, table);

        // Unlike Msg 1916, this one names the table, so it can only be raised
        // once the target has bound — probe-confirmed: a filtered index over a
        // missing table reports the missing object instead.
        if (ignoreDupKey && filter is not null)
            throw SimulatedSqlException.IgnoreDupKeyOnFilteredIndex("create", indexName, qualifiedTableName);

        if (filter is not null)
            RejectComputedColumnInIndexFilter(context.Batch, table, indexName, qualifiedTableName, filter);

        foreach (var existing in table.Indexes)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(existing.Name, indexName))
                throw SimulatedSqlException.IndexAlreadyExists(indexName, qualifiedTableName);
        }
        foreach (var kc in table.KeyConstraints)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(kc.Name, indexName))
                throw SimulatedSqlException.IndexAlreadyExists(indexName, qualifiedTableName);
        }

        // A table can carry at most one clustered index — a clustered PK/UQ
        // constraint or a prior CREATE CLUSTERED INDEX. Msg 1902 names the
        // existing one (a default PK is clustered).
        if (isClustered)
        {
            var existingClustered =
                table.KeyConstraints.FirstOrDefault(k => k.IsClustered)?.Name
                ?? table.Indexes.FirstOrDefault(ix => ix.IsClustered)?.Name;
            if (existingClustered is not null)
                throw SimulatedSqlException.MoreThanOneClusteredIndex(table.Name, existingClustered);
        }

        var resolvedKeyColumns = new IndexKeyColumn[keyColumns.Count];
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var fullOrdinal = ResolveColumnOrdinal(context.Batch.CurrentDatabase.Collation, table, keyColumns[i].Name);
            RejectComputedKeyColumnNotIndexable(context.Batch, table, table.Columns[fullOrdinal], indexName, viaConstraint: false);
            resolvedKeyColumns[i] = new IndexKeyColumn(table.StorageOrdinals[fullOrdinal], fullOrdinal, keyColumns[i].IsDescending);
        }
        var resolvedIncludeColumns = new int[includeColumnNames.Count];
        var resolvedIncludeOrdinals = new int[includeColumnNames.Count];
        for (var i = 0; i < includeColumnNames.Count; i++)
        {
            var fullOrdinal = ResolveColumnOrdinal(context.Batch.CurrentDatabase.Collation, table, includeColumnNames[i]);
            resolvedIncludeColumns[i] = table.StorageOrdinals[fullOrdinal];
            resolvedIncludeOrdinals[i] = fullOrdinal;
        }

        var index = new StoredIndex(
            indexName,
            context.CurrentDatabase.AllocateObjectId(),
            isUnique,
            isClustered,
            resolvedKeyColumns,
            resolvedIncludeColumns,
            resolvedIncludeOrdinals,
            filter,
            filterDefinition,
            ignoreDupKey);

        // A filtered index or one over a computed column stores the value of
        // an expression, so real refuses to build it from a session whose SET
        // options would read that expression differently (Msg 1934, naming
        // every offending option). A plain index over plain columns is
        // unaffected (probe-confirmed).
        if ((filter is not null || IndexCoversComputedColumn(table, index)) && IncorrectSetOptionNames(context) is { } setOptions)
            throw SimulatedSqlException.IncorrectSetOptions("CREATE INDEX", setOptions);

        if (isUnique)
            ValidateExistingRowsForUniqueIndex(table, index, context.Batch, qualifiedTableName);

        table.Indexes.Add(index);
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
        BatchContext batch, HeapTable table, string indexName, string qualifiedTableName, BooleanExpression filter)
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
            throw SimulatedSqlException.FilteredIndexOnComputedColumn(indexName, qualifiedTableName, offending);
    }

    /// <summary>
    /// Builds the indexes declared inline in a CREATE TABLE (the table-level
    /// <c>INDEX ix (cols)</c> and column-level <c>col type INDEX ix</c> forms)
    /// against the freshly-created <paramref name="table"/>. Each maps to the
    /// same <see cref="StoredIndex"/> the standalone CREATE INDEX builds
    /// (catalog metadata + seek acceleration); the inline grammar exposes no
    /// UNIQUE / INCLUDE / filter forms, so those stay defaulted.
    /// </summary>
    private static void AddInlineIndexes(ParserContext context, HeapTable table, string schemaName, List<PendingInlineIndex> pendingIndexes)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        var qualifiedTableName = $"{schemaName}.{table.Name}";
        foreach (var pending in pendingIndexes)
        {
            foreach (var existing in table.Indexes)
            {
                if (collation.Equals(existing.Name, pending.Name))
                    throw SimulatedSqlException.IndexAlreadyExists(pending.Name, qualifiedTableName);
            }
            foreach (var kc in table.KeyConstraints)
            {
                if (collation.Equals(kc.Name, pending.Name))
                    throw SimulatedSqlException.IndexAlreadyExists(pending.Name, qualifiedTableName);
            }
            if (pending.IsClustered)
            {
                var existingClustered =
                    table.KeyConstraints.FirstOrDefault(k => k.IsClustered)?.Name
                    ?? table.Indexes.FirstOrDefault(ix => ix.IsClustered)?.Name;
                if (existingClustered is not null)
                    throw SimulatedSqlException.MoreThanOneClusteredIndex(table.Name, existingClustered);
            }

            var keyColumns = new IndexKeyColumn[pending.Columns.Length];
            for (var i = 0; i < pending.Columns.Length; i++)
            {
                var fullOrdinal = ResolveColumnOrdinal(collation, table, pending.Columns[i].ColumnName);
                keyColumns[i] = new IndexKeyColumn(table.StorageOrdinals[fullOrdinal], fullOrdinal, pending.Columns[i].IsDescending);
            }
            table.Indexes.Add(new StoredIndex(
                pending.Name,
                context.CurrentDatabase.AllocateObjectId(),
                isUnique: false,
                pending.IsClustered,
                keyColumns,
                [],
                [],
                filter: null,
                filterDefinition: null,
                // The inline CREATE TABLE index grammar exposes no WITH clause.
                ignoreDupKey: false));
        }
    }

    private static int ResolveColumnOrdinal(Collation collation, HeapTable table, string columnName)
    {
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (collation.Equals(table.Columns[i].Name, columnName))
                return i;
        }
        throw SimulatedSqlException.IndexColumnMissing(columnName);
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

            if (!seen.Add(new SqlValueKey(key)))
                throw SimulatedSqlException.DuplicateKeyOnCreate(qualifiedTableName, index.Name, FormatIndexKeyValues(key));
        }
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
}
