using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Built-in system TVF <c>fn_virtualfilestats</c> (invoked bare or
    /// <c>sys.</c>-qualified). Two args <c>(database_id, file_id)</c>, both
    /// nullable; <c>NULL</c> is the wildcard (all databases / all files),
    /// probe-confirmed against SQL Server 2025 (2026-07-19). A non-NULL id
    /// that names no database / file yields zero rows (including negative
    /// ids such as <c>-1</c>). Column shape mirrors real:
    /// <c>(DbId smallint, FileId smallint, TimeStamp bigint, NumberReads
    /// bigint, BytesRead bigint, IoStallReadMS bigint, NumberWrites bigint,
    /// BytesWritten bigint, IoStallWriteMS bigint, IoStallMS bigint,
    /// BytesOnDisk bigint, FileHandle varbinary(8))</c>.
    /// </summary>
    /// <remarks>
    /// One row per file of each database, as <c>sys.dm_io_virtual_file_stats</c>
    /// lists them: <c>BytesOnDisk</c> is the file's reported size and
    /// <c>TimeStamp</c> the milliseconds since the simulation started, while
    /// every IO counter is 0 and the <c>FileHandle</c> all zeros, the honest
    /// reading for an in-process store that performs no disk IO (probed
    /// 2026-10-16: real's <c>BytesOnDisk</c> is <c>size</c> × 8192 for each of
    /// <c>tempdb</c>'s nine files). The legacy <c>::fn_virtualfilestats(...)</c>
    /// prefix reaches here as the bare name (see <c>ParseLegacyFunctionPrefix</c>).
    /// </remarks>
    public static Selection ParseVirtualFileStats(ParserContext context, string functionName)
    {
        // On entry the cursor rests on the function name's leaf segment
        // (ParseObjectName leaves Token on the leaf); advance to the '('.
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var dbArg = Expression.Parse(context);

        if (context.Token is Operator { Character: ')' })
            throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var fileArg = Expression.Parse(context);

        // A trailing comma means a third argument was supplied.
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.TooManyArgumentsToFunction(functionName);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        SqlType[] schema =
        [
            SqlType.SmallInt, SqlType.SmallInt, SqlType.BigInt, SqlType.BigInt,
            SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt,
            SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, VarbinarySqlType.Get(8),
        ];
        string[] columnNames =
        [
            "DbId", "FileId", "TimeStamp", "NumberReads", "BytesRead", "IoStallReadMS",
            "NumberWrites", "BytesWritten", "IoStallWriteMS", "IoStallMS", "BytesOnDisk", "FileHandle",
        ];

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateVirtualFileStatsRows(schema, dbArg, fileArg, batch, outerResolver));
    }

    private static IEnumerable<byte[]> EnumerateVirtualFileStatsRows(
        SqlType[] schema,
        Expression dbExpr,
        Expression fileExpr,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var runtime = new RuntimeContext(resolver, batch);
        var dbFilter = EvalNullableInt(dbExpr, runtime);
        var fileFilter = EvalNullableInt(fileExpr, runtime);

        var simulation = batch.Connection.Simulation;
        var timeStamp = SqlValue.FromInt64(Environment.TickCount64 - simulation.StartTicks);
        var zero = SqlValue.FromInt64(0);
        var fileHandle = SqlValue.FromVarbinary(VarbinarySqlType.Get(8), new byte[8]);
        foreach (var (database, id) in DbId.DatabasesWithIds(simulation))
        {
            if (dbFilter is { } wantDb && wantDb != id)
                continue;
            foreach (var file in database.FilesInOrder())
            {
                if (fileFilter is { } wantFile && wantFile != file.FileId)
                    continue;
                yield return RowEncoder.EncodeRow(schema,
                [
                    SqlValue.FromInt16(id),
                    SqlValue.FromInt16((short)file.FileId),
                    timeStamp,
                    zero,
                    zero,
                    zero,
                    zero,
                    zero,
                    zero,
                    zero,
                    SqlValue.FromInt64(BuiltInResources.FileSizePages(database, file) * 8192L),
                    fileHandle,
                ]);
            }
        }
    }

    private static int? EvalNullableInt(Expression expr, RuntimeContext runtime)
    {
        var value = expr.Run(runtime);
        return value.IsNull ? null : ScalarArguments.CoerceToInt(value);
    }
}
