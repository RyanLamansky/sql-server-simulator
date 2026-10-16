using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// The index and statistics system TVFs — <c>sys.dm_db_index_physical_stats</c>,
    /// <c>dm_db_index_operational_stats</c>, <c>dm_db_stats_properties</c>,
    /// <c>dm_db_stats_histogram</c>, <c>dm_db_incremental_stats_properties</c>
    /// and <c>dm_db_missing_index_columns</c> — by name, or null for another.
    /// Shapes probed 2026-10-05 against SQL Server 2025. A non-null
    /// <paramref name="database"/> — a three-part name's — is where the rows
    /// are read, after the arguments evaluate in the session's database.
    /// </summary>
    private static Selection? ParseIndexDmv(ParserContext context, string leaf, string functionName, Database? database)
    {
        Span<char> upper = stackalloc char[leaf.Length];
        _ = leaf.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "DM_DB_INCREMENTAL_STATS_PROPERTIES" => ParseSystemTvf(context, functionName, database, 2, IncrementalStatsColumns, static (_, _) => [], notNullColumns: 2),
            "DM_DB_INDEX_OPERATIONAL_STATS" => ParseSystemTvf(context, functionName, database, 4, OperationalStatsColumns,
                static (batch, args) => BuiltInResources.OperationalStats(batch, IntArgument(args[0]), IntArgument(args[1]), IntArgument(args[2]), IntArgument(args[3])),
                notNullColumns: BuiltInResources.OperationalStatsColumnCount),
            "DM_DB_INDEX_PHYSICAL_STATS" => ParseSystemTvf(context, functionName, database, 5, BuiltInResources.IndexPhysicalStatsColumns,
                static (batch, args) => BuiltInResources.IndexPhysicalStats(batch, IntArgument(args[0]), IntArgument(args[1]), IntArgument(args[2]), IntArgument(args[3]),
                    args[4].IsNull ? null : args[4].CoerceTo(SqlType.NVarchar).AsString)),
            "DM_DB_MISSING_INDEX_COLUMNS" => ParseSystemTvf(context, functionName, database, 1, MissingIndexColumnsColumns, static (_, _) => [], notNullColumns: 1),
            "DM_DB_STATS_HISTOGRAM" => ParseSystemTvf(context, functionName, database, 2, StatsHistogramColumns,
                static (batch, args) => BuiltInResources.StatsHistogram(batch, IntArgument(args[0]), IntArgument(args[1])), notNullColumns: 3),
            "DM_DB_STATS_PROPERTIES" => ParseSystemTvf(context, functionName, database, 2, StatsPropertiesColumns,
                static (batch, args) => BuiltInResources.StatsProperties(batch, IntArgument(args[0]), IntArgument(args[1])), notNullColumns: 2),
            _ => null,
        };
    }

    private static int? IntArgument(SqlValue value) => value.IsNull ? null : ScalarArguments.CoerceToInt(value);

    private static readonly (string Name, SqlType Type)[] StatsPropertiesColumns =
    [
        ("object_id", SqlType.Int32), ("stats_id", SqlType.Int32), ("last_updated", SqlType.GetDateTime2(7)), ("rows", SqlType.BigInt),
        ("rows_sampled", SqlType.BigInt), ("steps", SqlType.Int32), ("unfiltered_rows", SqlType.BigInt), ("modification_counter", SqlType.BigInt),
        ("persisted_sample_percent", SqlType.Float),
    ];

    private static readonly (string Name, SqlType Type)[] StatsHistogramColumns =
    [
        ("object_id", SqlType.Int32), ("stats_id", SqlType.Int32), ("step_number", SqlType.Int32), ("range_high_key", SqlType.SqlVariant),
        ("range_rows", SqlType.Real), ("equal_rows", SqlType.Real), ("distinct_range_rows", SqlType.BigInt), ("average_range_rows", SqlType.Real),
    ];

    private static readonly (string Name, SqlType Type)[] IncrementalStatsColumns =
    [
        ("object_id", SqlType.Int32), ("stats_id", SqlType.Int32), ("partition_number", SqlType.Int32), ("last_updated", SqlType.GetDateTime2(7)),
        ("rows", SqlType.BigInt), ("rows_sampled", SqlType.BigInt), ("steps", SqlType.Int32), ("unfiltered_rows", SqlType.BigInt),
        ("modification_counter", SqlType.BigInt),
    ];

    private static readonly (string Name, SqlType Type)[] MissingIndexColumnsColumns =
    [
        ("column_id", SqlType.Int32), ("column_name", NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit)),
        ("column_usage", NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit)),
    ];

    private static readonly (string Name, SqlType Type)[] OperationalStatsColumns =
    [
        ("database_id", SqlType.SmallInt), ("object_id", SqlType.Int32), ("index_id", SqlType.Int32), ("partition_number", SqlType.Int32),
        ("hobt_id", SqlType.BigInt), ("leaf_insert_count", SqlType.BigInt), ("leaf_delete_count", SqlType.BigInt), ("leaf_update_count", SqlType.BigInt),
        ("leaf_ghost_count", SqlType.BigInt), ("nonleaf_insert_count", SqlType.BigInt), ("nonleaf_delete_count", SqlType.BigInt),
        ("nonleaf_update_count", SqlType.BigInt), ("leaf_allocation_count", SqlType.BigInt), ("nonleaf_allocation_count", SqlType.BigInt),
        ("leaf_page_merge_count", SqlType.BigInt), ("nonleaf_page_merge_count", SqlType.BigInt), ("range_scan_count", SqlType.BigInt),
        ("singleton_lookup_count", SqlType.BigInt), ("forwarded_fetch_count", SqlType.BigInt), ("lob_fetch_in_pages", SqlType.BigInt),
        ("lob_fetch_in_bytes", SqlType.BigInt), ("lob_orphan_create_count", SqlType.BigInt), ("lob_orphan_insert_count", SqlType.BigInt),
        ("row_overflow_fetch_in_pages", SqlType.BigInt), ("row_overflow_fetch_in_bytes", SqlType.BigInt), ("column_value_push_off_row_count", SqlType.BigInt),
        ("column_value_pull_in_row_count", SqlType.BigInt), ("row_lock_count", SqlType.BigInt), ("row_lock_wait_count", SqlType.BigInt),
        ("row_lock_wait_in_ms", SqlType.BigInt), ("page_lock_count", SqlType.BigInt), ("page_lock_wait_count", SqlType.BigInt),
        ("page_lock_wait_in_ms", SqlType.BigInt), ("index_lock_promotion_attempt_count", SqlType.BigInt), ("index_lock_promotion_count", SqlType.BigInt),
        ("page_latch_wait_count", SqlType.BigInt), ("page_latch_wait_in_ms", SqlType.BigInt), ("page_io_latch_wait_count", SqlType.BigInt),
        ("page_io_latch_wait_in_ms", SqlType.BigInt), ("tree_page_latch_wait_count", SqlType.BigInt), ("tree_page_latch_wait_in_ms", SqlType.BigInt),
        ("tree_page_io_latch_wait_count", SqlType.BigInt), ("tree_page_io_latch_wait_in_ms", SqlType.BigInt), ("page_compression_attempt_count", SqlType.BigInt),
        ("page_compression_success_count", SqlType.BigInt), ("version_generated_inrow", SqlType.BigInt), ("version_generated_offrow", SqlType.BigInt),
        ("ghost_version_inrow", SqlType.BigInt), ("ghost_version_offrow", SqlType.BigInt), ("insert_over_ghost_version_inrow", SqlType.BigInt),
        ("insert_over_ghost_version_offrow", SqlType.BigInt),
    ];

    /// <summary>
    /// A system TVF of exactly <paramref name="arity"/> arguments, each an
    /// expression or <c>DEFAULT</c> (read as NULL), whose rows
    /// <paramref name="rows"/> produces when it runs, its leading
    /// <paramref name="notNullColumns"/> columns declared NOT NULL as real
    /// declares them (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static Selection ParseSystemTvf(ParserContext context, string functionName, Database? database, int arity, (string Name, SqlType Type)[] columns, Func<BatchContext, SqlValue[], List<SqlValue[]>> rows, int notNullColumns = 0, byte arityState = 2)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var arguments = new Expression?[arity];
        var afterParenthesis = context.SaveCheckpoint();
        if (arity > 0 && context.GetNextRequired() is Operator { Character: ')' })
            throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, arityState);
        context.RestoreCheckpoint(afterParenthesis);
        for (var i = 0; ; i++)
        {
            context.MoveNextRequired();
            Expression? argument = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
                context.MoveNextRequired();
            else
                argument = Expression.Parse(context);
            if (i < arity)
                arguments[i] = argument;
            if (context.Token is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (i < arity - 1)
                throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, arityState);
            if (i >= arity)
                throw SimulatedSqlException.TooManyArgumentsToFunction(functionName, arityState);
            break;
        }
        context.MoveNextOptional();
        var schema = Array.ConvertAll(columns, static column => column.Type);
        var names = Array.ConvertAll(columns, static column => column.Name);
        var nullability = new bool[columns.Length];
        for (var i = notNullColumns; i < nullability.Length; i++)
            nullability[i] = true;
        return new Selection(schema, names,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) =>
            {
                var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
                var values = new SqlValue[arity];
                for (var i = 0; i < arity; i++)
                    values[i] = arguments[i]?.Run(runtime) ?? SqlValue.Null(SqlType.Int32);
                List<SqlValue[]> produced;
                var scope = database is null ? default : ModuleDatabaseScope.Enter(batch.Connection, database);
                try
                {
                    produced = rows(batch, values);
                }
                finally
                {
                    scope.Exit();
                }
                var encoded = new List<byte[]>(produced.Count);
                foreach (var row in produced)
                    encoded.Add(RowEncoder.EncodeRow(schema, row));
                return encoded;
            })
        {
            ColumnNullability = nullability,
        };
    }
}
