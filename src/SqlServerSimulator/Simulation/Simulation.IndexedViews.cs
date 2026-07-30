using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using StoredIndex = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Records a <c>CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED] INDEX … ON
    /// &lt;view&gt;</c> — an indexed (materialized) view. Applies the three
    /// probe-confirmed gates in real SQL Server 2025's order, resolves the
    /// key / INCLUDE columns to <b>view OUTPUT-column</b> ordinals, validates
    /// the current view rows for duplicates when UNIQUE (Msg 1505), records
    /// the index on <see cref="View.Indexes"/>, and wires the base tables so a
    /// later base-table INSERT / UPDATE re-validates uniqueness (Msg 2601).
    /// </summary>
    /// <remarks>
    /// Gate order (probe-confirmed 2026-07-17): <strong>Msg 1939</strong> (view
    /// not schema bound — uses the view's leaf name) fires first; then
    /// <strong>Msg 1941</strong> (a non-unique CLUSTERED index is never
    /// allowed on a view); then <strong>Msg 1940</strong> (any index requires
    /// an existing unique clustered index, i.e. the first index on a view must
    /// be UNIQUE CLUSTERED). The qualifying battery real runs after those —
    /// determinism, DISTINCT / TOP / OUTER JOIN / set-op / subquery shape, and
    /// the aggregate rules — follows in
    /// <see cref="EnforceIndexedViewQualifies"/>. See
    /// <c>docs/claude/indexes.md</c>.
    /// </remarks>
    private void CreateIndexOnView(
        ParserContext context, View view, string indexName, bool isUnique, bool isClustered,
        List<(string Name, bool IsDescending)> keyColumns, List<string> includeColumnNames,
        BooleanExpression? filter, string? filterDefinition)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        var qualifiedViewName = $"{view.Schema.Name}.{view.Name}";

        if (!view.IsSchemaBound)
            throw SimulatedSqlException.CannotIndexViewNotSchemaBound(view.Name);
        if (isClustered && !isUnique)
            throw SimulatedSqlException.CannotIndexViewNonUniqueClustered(qualifiedViewName);
        var hasUniqueClustered = false;
        foreach (var existing in view.Indexes)
        {
            if (existing.IsUnique && existing.IsClustered)
            {
                hasUniqueClustered = true;
                break;
            }
        }
        if (!hasUniqueClustered && !(isUnique && isClustered))
            throw SimulatedSqlException.CannotIndexViewNoUniqueClustered(qualifiedViewName);

        EnforceIndexedViewQualifies(context, view, indexName);

        foreach (var existing in view.Indexes)
        {
            if (collation.Equals(existing.Name, indexName))
                throw SimulatedSqlException.IndexAlreadyExists(indexName, qualifiedViewName);
        }

        var resolvedKeyColumns = new IndexKeyColumn[keyColumns.Count];
        for (var i = 0; i < keyColumns.Count; i++)
        {
            // View row bytes are encoded in OutputColumns order, so the output
            // ordinal doubles as both the storage ordinal (decode) and the
            // column ordinal (sys.index_columns.column_id = ordinal + 1).
            var ordinal = ResolveViewOutputOrdinal(collation, view, keyColumns[i].Name);
            resolvedKeyColumns[i] = new IndexKeyColumn(ordinal, ordinal, keyColumns[i].IsDescending);
        }
        var resolvedIncludeColumns = new int[includeColumnNames.Count];
        var resolvedIncludeOrdinals = new int[includeColumnNames.Count];
        for (var i = 0; i < includeColumnNames.Count; i++)
        {
            var ordinal = ResolveViewOutputOrdinal(collation, view, includeColumnNames[i]);
            resolvedIncludeColumns[i] = ordinal;
            resolvedIncludeOrdinals[i] = ordinal;
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
            // Unreachable with the option set: an index over a view carrying
            // IGNORE_DUP_KEY is rejected with Msg 1990 before this point.
            ignoreDupKey: false);

        if (isUnique)
        {
            var (schema, rows) = this.MaterializeViewForEnforcement(context.Batch, view);
            if (FindFirstDuplicateViewKey(index, schema, rows) is { } dupKey)
                throw SimulatedSqlException.DuplicateKeyOnCreate(qualifiedViewName, indexName, FormatIndexKeyValues(dupKey));
        }

        view.Indexes.Add(index);
        this.RegisterViewDependencies(context.Batch, view);
    }

    private static int ResolveViewOutputOrdinal(Collation collation, View view, string columnName)
    {
        for (var i = 0; i < view.OutputColumns.Length; i++)
        {
            if (collation.Equals(view.OutputColumns[i].Name, columnName))
                return i;
        }
        throw SimulatedSqlException.IndexColumnMissing(columnName);
    }

    /// <summary>
    /// Re-evaluates every indexed view that references <paramref name="mutatedTable"/>
    /// as a base and enforces each view's UNIQUE index, raising
    /// <strong>Msg 2601</strong> (naming the schema-qualified view + index and
    /// rendering the duplicate key) on a collision. Called after an INSERT /
    /// UPDATE has applied its heap writes, so the re-evaluation sees the new
    /// base rows; a violation propagates and <c>RunMutation</c>'s undo log
    /// rolls the statement back (statement atomicity). Zero-cost when the
    /// table has no dependent indexed views. DELETE never triggers this — a
    /// valid indexed view is an inner-join / aggregate projection, so removing
    /// base rows can only remove or reduce view rows, never create a new
    /// duplicate key (verified against SQL Server 2025, 2026-07-17).
    /// </summary>
    internal void EnforceIndexedViews(HeapTable mutatedTable, BatchContext batch)
    {
        if (mutatedTable.DependentIndexedViews.Count == 0)
            return;
        foreach (var view in mutatedTable.DependentIndexedViews)
        {
            var hasUnique = false;
            foreach (var index in view.Indexes)
            {
                if (index.IsUnique)
                {
                    hasUnique = true;
                    break;
                }
            }
            if (!hasUnique)
                continue;

            var (schema, rows) = this.MaterializeViewForEnforcement(batch, view);
            var qualifiedViewName = $"{view.Schema.Name}.{view.Name}";
            foreach (var index in view.Indexes)
            {
                if (!index.IsUnique)
                    continue;
                if (FindFirstDuplicateViewKey(index, schema, rows) is { } dupKey)
                    throw SimulatedSqlException.ViolationOfUniqueIndex(index.Name, qualifiedViewName, FormatIndexKeyValues(dupKey));
            }
        }
    }

    /// <summary>
    /// Scans the materialized view rows for the first key-tuple collision
    /// under <paramref name="index"/>, returning the duplicate key or null.
    /// NULLs compare equal (SQL Server's UNIQUE-index rule), matching the
    /// heap-table unique-index path (<c>SqlValue.Equals</c>).
    /// </summary>
    private static SqlValue[]? FindFirstDuplicateViewKey(StoredIndex index, SqlType[] schema, List<byte[]> rows)
    {
        var seen = new List<SqlValue[]>();
        foreach (var rowBytes in rows)
        {
            var key = new SqlValue[index.KeyColumns.Length];
            for (var k = 0; k < key.Length; k++)
                key[k] = RowDecoder.DecodeColumn(schema, rowBytes, index.KeyColumns[k].StorageOrdinal);
            foreach (var prior in seen)
            {
                var match = true;
                for (var k = 0; k < key.Length; k++)
                {
                    if (!prior[k].Equals(key[k]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return key;
            }
            seen.Add(key);
        }
        return null;
    }

    /// <summary>
    /// Executes a view body once and materializes its rows into a list, paired
    /// with the body's result schema (for column decode). Mirrors
    /// <see cref="InvokeViewCore"/>'s child-batch construction but returns the
    /// full result eagerly (enforcement needs to re-scan the rows per index).
    /// </summary>
    private (SqlType[] Schema, List<byte[]> Rows) MaterializeViewForEnforcement(BatchContext outerBatch, View view)
    {
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();
        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // view.BodyText is the view's pre-validated stored body, not external input
        bodyCommand.CommandText = view.BodyText;
#pragma warning restore CA2100
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var dummyFrame = new UdfFrame(SqlType.Int32);
        var innerBatch = new BatchContext(bodyCommand, variables, dummyFrame) { SuppressDiagnosticsResolution = true };
        connection.NestingLevel++;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextRequired();
            var bodySelection = Selection.Parse(parser, depth: 0);
            var resultSet = bodySelection.Execute(innerBatch, outerResolver: null);
            var rows = new List<byte[]>();
            foreach (var rowBytes in resultSet.RowBytes)
                rows.Add(rowBytes);
            return (resultSet.Schema, rows);
        }
        finally
        {
            connection.NestingLevel--;
        }
    }

    /// <summary>
    /// Collects the view's base tables (walking JOINs / subqueries / nested
    /// schema-bound views) and registers the view on each base table's
    /// <see cref="HeapTable.DependentIndexedViews"/> so their DML re-validates
    /// it. Runs once per CREATE INDEX at the cold path. View-dependency
    /// tracking isn't otherwise modeled, so this is a targeted re-parse of the
    /// body under a resolution sink rather than a shared dependency graph.
    /// </summary>
    private void RegisterViewDependencies(BatchContext outerBatch, View view)
    {
        var tables = new HashSet<HeapTable>();
        var visited = new HashSet<View>();
        this.CollectViewBaseTables(outerBatch, view, tables, visited);
        view.ReferencedBaseTables = [.. tables];
        foreach (var table in tables)
        {
            if (!table.DependentIndexedViews.Contains(view))
                table.DependentIndexedViews.Add(view);
        }
    }

    private void CollectViewBaseTables(BatchContext outerBatch, View view, HashSet<HeapTable> tables, HashSet<View> visited)
    {
        if (!visited.Add(view))
            return;
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            return;
        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // view.BodyText is the view's pre-validated stored body, not external input
        bodyCommand.CommandText = view.BodyText;
#pragma warning restore CA2100
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var dummyFrame = new UdfFrame(SqlType.Int32);
        var innerBatch = new BatchContext(bodyCommand, variables, dummyFrame) { SuppressDiagnosticsResolution = true };
        var nestedViews = new HashSet<View>();
        innerBatch.DependencySink = (tables, nestedViews);
        connection.NestingLevel++;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextRequired();
            _ = Selection.Parse(parser, depth: 0);
        }
        finally
        {
            connection.NestingLevel--;
        }
        foreach (var nested in nestedViews)
            this.CollectViewBaseTables(outerBatch, nested, tables, visited);
    }

    /// <summary>
    /// The qualifying battery real SQL Server runs when an index is created on
    /// a view. Every shape below creates fine as a *view* and fails only here
    /// (probe-confirmed against SQL Server 2025), so the check re-parses the
    /// stored body with an <see cref="IndexedViewShape"/> collector installed
    /// rather than inspecting anything stamped at CREATE VIEW.
    /// <para><strong>Gate order is the simulator's own.</strong> Each rejection
    /// was probed in isolation, so real's precedence when a view violates
    /// several at once isn't pinned; a body with both DISTINCT and TOP may name
    /// the other one on real.</para>
    /// </summary>
    private void EnforceIndexedViewQualifies(ParserContext context, View view, string indexName)
    {
        var shape = AnalyzeIndexedViewShape(context.Batch, view);
        // Real database-qualifies the view in every message of this family.
        var qualified = $"{context.Batch.CurrentDatabase.Name}.{view.Schema.Name}.{view.Name}";

        if (shape.NondeterministicFunction is { } nondeterministic)
            throw SimulatedSqlException.IndexedViewIsNondeterministic(qualified, nondeterministic);
        if (shape.SelfJoinedTable is { } selfJoined)
        {
            throw SimulatedSqlException.IndexedViewHasSelfJoin(
                qualified,
                $"{context.Batch.CurrentDatabase.Name}.{SchemaNameOf(context.Batch.CurrentDatabase, selfJoined)}.{selfJoined.Name}");
        }
        if (shape.HasDistinct)
            throw SimulatedSqlException.IndexedViewHasDistinct(qualified);
        if (shape.HasTopOrOffset)
            throw SimulatedSqlException.IndexedViewHasTopOrOffset(qualified);
        if (shape.HasOuterJoin)
            throw SimulatedSqlException.IndexedViewHasOuterJoin(qualified);
        if (shape.HasSetOperation)
            throw SimulatedSqlException.IndexedViewHasSetOperator(qualified);
        if (shape.HasSubquery)
            throw SimulatedSqlException.IndexedViewHasSubquery(qualified);

        foreach (var aggregate in shape.Aggregates)
        {
            // COUNT has its own message pointing at COUNT_BIG; the rest of the
            // disallowed family shares Msg 10125 and names itself. Aggregates
            // outside both sets (STRING_AGG and friends) are left alone rather
            // than guessed at — an unprobed rejection would be the
            // over-restrictive direction.
            if (aggregate == AggregateKind.Count)
                throw SimulatedSqlException.IndexedViewUsesCount(qualified);
            if (DisallowedAggregateName(aggregate) is { } name)
                throw SimulatedSqlException.IndexedViewHasDisallowedAggregate(qualified, name);
        }

        if (shape.HasGroupBy && !shape.Aggregates.Contains(AggregateKind.CountBig))
            throw SimulatedSqlException.IndexedViewMissingCountBig(qualified);
        if (shape.SumsNullableExpression)
            throw SimulatedSqlException.IndexedViewSumsNullableExpression(indexName, qualified);
    }

    /// <summary>
    /// The name Msg 10125 embeds for an aggregate an indexed view may not use,
    /// or null when the aggregate is allowed (or not classified).
    /// </summary>
    private static string? DisallowedAggregateName(AggregateKind kind) => kind switch
    {
        AggregateKind.Avg => "AVG",
        AggregateKind.Max => "MAX",
        AggregateKind.Min => "MIN",
        AggregateKind.Stdev => "STDEV",
        AggregateKind.StdevP => "STDEVP",
        AggregateKind.Var => "VAR",
        AggregateKind.VarP => "VARP",
        _ => null,
    };

    /// <summary>Schema name for a table, resolved by its schema id.</summary>
    private static string SchemaNameOf(Database database, HeapTable table)
    {
        foreach (var (name, schema) in database.Schemas)
        {
            if (schema.SchemaId == table.SchemaId)
                return name;
        }
        return Database.DefaultSchemaName;
    }

    /// <summary>
    /// Re-parses a view's stored body purely to collect the structural facts
    /// <see cref="EnforceIndexedViewQualifies"/> judges. Mirrors
    /// <c>InvokeViewCore</c>'s child-batch setup but stops at parse — nothing
    /// is executed, so the body's side-effect-free shape is all that's read.
    /// </summary>
    private IndexedViewShape AnalyzeIndexedViewShape(BatchContext outerBatch, View view)
    {
        using var bodyCommand = new SimulatedDbCommand(this, outerBatch.Connection);
#pragma warning disable CA2100 // view.BodyText is the view's pre-validated stored body, not external input
        bodyCommand.CommandText = view.BodyText;
#pragma warning restore CA2100
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var innerBatch = new BatchContext(bodyCommand, variables, new UdfFrame(SqlType.Int32))
        {
            SuppressDiagnosticsResolution = true,
        };

        var shape = new IndexedViewShape();
        var parser = innerBatch.Parser;
        parser.IndexedViewShapeCollector = shape;
        parser.MoveNextRequired();
        _ = Selection.Parse(parser, depth: 0);
        shape.HasSubquery = parser.SubqueriesParsed > 0;
        return shape;
    }
}
