using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    private static readonly SqlType[] VirtualFileStatsDmvSchema =
    [
        SqlType.SmallInt, SqlType.SmallInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt,
        SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt,
        SqlType.BigInt, VarbinarySqlType.Get(8), SqlType.BigInt, SqlType.BigInt,
    ];

    private static readonly string[] VirtualFileStatsDmvColumnNames =
    [
        "database_id", "file_id", "sample_ms", "num_of_reads", "num_of_bytes_read", "io_stall_read_ms",
        "io_stall_queued_read_ms", "num_of_writes", "num_of_bytes_written", "io_stall_write_ms",
        "io_stall_queued_write_ms", "io_stall", "size_on_disk_bytes", "file_handle", "num_of_pushed_reads",
        "num_of_pushed_bytes_returned",
    ];

    /// <summary>
    /// Built-in system TVF <c>sys.dm_io_virtual_file_stats(database_id, file_id)</c>:
    /// one row per file of every database, NULL or <c>DEFAULT</c> matching
    /// every database or file, a file's size on disk its reported size, and
    /// every I/O counter 0 for a store that does no disk I/O. Gated by
    /// <c>VIEW SERVER PERFORMANCE STATE</c> (Msg 300), shape probed
    /// 2026-09-30 against SQL Server 2025.
    /// </summary>
    public static Selection ParseVirtualFileStatsDmv(ParserContext context, string functionName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var arguments = new Expression?[2];
        for (var i = 0; ; i++)
        {
            context.MoveNextRequired();
            Expression? argument = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
                context.MoveNextRequired();
            else
                argument = Expression.Parse(context);
            if (i < arguments.Length)
                arguments[i] = argument;
            if (context.Token is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (i < arguments.Length - 1)
                throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName);
            if (i >= arguments.Length)
                throw SimulatedSqlException.TooManyArgumentsToFunction(functionName);
            break;
        }
        context.MoveNextOptional();
        return new Selection(VirtualFileStatsDmvSchema, VirtualFileStatsDmvColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateVirtualFileStatsDmv(arguments[0], arguments[1], batch, outerResolver));
    }

    private static List<byte[]> EnumerateVirtualFileStatsDmv(Expression? databaseExpr, Expression? fileExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: false);
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        var databaseFilter = databaseExpr is null ? null : EvalNullableInt(databaseExpr, runtime);
        var fileFilter = fileExpr is null ? null : EvalNullableInt(fileExpr, runtime);
        var simulation = batch.Connection.Simulation;
        var sampleMs = SqlValue.FromInt64(Environment.TickCount64 - simulation.StartTicks);
        var zero = SqlValue.FromInt64(0);
        var handle = SqlValue.FromVarbinary(VarbinarySqlType.Get(8), new byte[8]);
        var rows = new List<byte[]>();
        foreach (var (database, id) in DbId.DatabasesWithIds(simulation))
        {
            if (databaseFilter is { } wantDatabase && wantDatabase != id)
                continue;
            foreach (var file in database.FilesInOrder())
            {
                if (fileFilter is { } wantFile && wantFile != file.FileId)
                    continue;
                rows.Add(RowEncoder.EncodeRow(VirtualFileStatsDmvSchema,
                [
                    SqlValue.FromInt16(id), SqlValue.FromInt16((short)file.FileId), sampleMs,
                    zero, zero, zero, zero, zero, zero, zero, zero, zero,
                    SqlValue.FromInt64(BuiltInResources.FileSizePages(database, file) * 8192L),
                    handle, zero, zero,
                ]));
            }
        }
        return rows;
    }
}
