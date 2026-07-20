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
    /// The simulator has no physical file model, so it reports one row per
    /// (database, <c>file_id 1</c>) with all IO counters and <c>BytesOnDisk</c>
    /// at 0 and an all-zero <c>FileHandle</c> — the honest reading for an
    /// in-process store that performs no disk IO. The wildcard / filter
    /// semantics still match real so a caller enumerating files gets the right
    /// row cardinality per database. The legacy <c>::fn_virtualfilestats(...)</c>
    /// prefix form isn't tokenized; the bare and <c>sys.</c>-qualified forms
    /// cover the documented invocations.
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

        // Only file_id 1 is modeled; a non-NULL file argument naming any other
        // file yields no rows (NULL is the all-files wildcard).
        if (fileFilter is { } wantFile && wantFile != 1)
            yield break;

        var fileHandle = new byte[8];
        foreach (var (_, id) in DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            if (dbFilter is { } wantDb && wantDb != id)
                continue;

            yield return RowEncoder.EncodeRow(schema,
            [
                SqlValue.FromInt16(id),
                SqlValue.FromInt16(1),
                SqlValue.FromInt64(0),
                SqlValue.FromInt64(0),
                SqlValue.FromInt64(0),
                SqlValue.FromInt64(0),
                SqlValue.FromInt64(0),
                SqlValue.FromInt64(0),
                SqlValue.FromInt64(0),
                SqlValue.FromInt64(0),
                SqlValue.FromInt64(0),
                SqlValue.FromVarbinary(VarbinarySqlType.Get(8), fileHandle),
            ]);
        }
    }

    private static int? EvalNullableInt(Expression expr, RuntimeContext runtime)
    {
        var value = expr.Run(runtime);
        return value.IsNull ? null : value.CoerceTo(SqlType.Int32).AsInt32;
    }
}
