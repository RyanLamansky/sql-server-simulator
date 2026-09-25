using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>The column count <c>sp_describe_first_result_set</c> shares with the DMV, which ends before the TDS columns.</summary>
    private const int DescribedColumnCount = 35;

    private static readonly SqlType[] DescribeDmvSchema = BuildDescribeDmvSchema();

    private static readonly string[] DescribeDmvColumnNames =
    [
        .. Simulation.DescribeColumnNames.AsSpan(0, DescribedColumnCount),
        "error_number", "error_severity", "error_state", "error_message", "error_type", "error_type_desc",
    ];

    private static SqlType[] BuildDescribeDmvSchema()
    {
        SqlType[] schema =
        [
            .. Simulation.DescribeSchema.AsSpan(0, DescribedColumnCount),
            SqlType.Int32, SqlType.Int32, SqlType.Int32, NVarcharSqlType.Get(2048, Collation.Baseline, Coercibility.Implicit),
            SqlType.Int32, NVarcharSqlType.Get(30, Collation.Baseline, Coercibility.Implicit),
        ];
        // The DMV declares system_type_name half as wide as the procedure does.
        schema[5] = NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);
        return schema;
    }

    /// <summary>
    /// Built-in system TVF <c>sys.dm_exec_describe_first_result_set(@tsql, @params, @browse_information_mode)</c>
    /// — <c>sp_describe_first_result_set</c>'s rows as a rowset, with the TDS
    /// columns replaced by six <c>error_*</c> columns: a batch that can't be
    /// described answers one row per error (its index in <c>column_ordinal</c>)
    /// rather than raising, and a NULL <c>@tsql</c> answers no rows.
    /// All three arguments are required.
    /// Probed 2026-09-25 against SQL Server 2025.
    /// </summary>
    public static Selection ParseDescribeFirstResultSet(ParserContext context, string functionName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var arguments = new Expression[3];
        for (var i = 0; ; i++)
        {
            context.MoveNextRequired();
            var argument = Expression.Parse(context);
            if (i < arguments.Length)
                arguments[i] = argument;
            if (context.Token is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (i < arguments.Length - 1)
                throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, 3);
            if (i >= arguments.Length)
                throw SimulatedSqlException.TooManyArgumentsToFunction(functionName, 3);
            break;
        }
        context.MoveNextOptional();
        return new Selection(DescribeDmvSchema, DescribeDmvColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateDescribeFirstResultSet(arguments[0], arguments[1], batch, outerResolver));
    }

    private static List<byte[]> EnumerateDescribeFirstResultSet(
        Expression tsqlExpr, Expression paramsExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        var tsql = tsqlExpr.Run(runtime);
        if (tsql.IsNull)
            return [];
        var parameters = paramsExpr.Run(runtime);

        var rows = new List<byte[]>();
        try
        {
            foreach (var described in batch.Connection.Simulation.DescribeFirstResult(batch, tsql.AsString, parameters))
            {
                var row = new SqlValue[DescribeDmvSchema.Length];
                described.AsSpan(0, DescribedColumnCount).CopyTo(row);
                for (var i = DescribedColumnCount; i < row.Length; i++)
                    row[i] = SqlValue.Null(DescribeDmvSchema[i]);
                rows.Add(RowEncoder.EncodeRow(DescribeDmvSchema, row));
            }
        }
        catch (SimulatedSqlException error)
        {
            rows.Clear();
            var ordinal = 0;
            foreach (var entry in error.Errors)
            {
                var row = new SqlValue[DescribeDmvSchema.Length];
                for (var i = 0; i < row.Length; i++)
                    row[i] = SqlValue.Null(DescribeDmvSchema[i]);
                var (type, typeDesc) = DescribeErrorType(entry.Number);
                row[1] = SqlValue.FromInt32(ordinal++);
                row[35] = SqlValue.FromInt32(entry.Number);
                row[36] = SqlValue.FromInt32(entry.Class);
                row[37] = SqlValue.FromInt32(entry.State);
                row[38] = SqlValue.FromNVarchar(entry.Message);
                row[39] = SqlValue.FromInt32(type);
                row[40] = SqlValue.FromNVarchar(typeDesc);
                rows.Add(RowEncoder.EncodeRow(DescribeDmvSchema, row));
            }
        }
        return rows;
    }

    /// <summary>
    /// The <c>error_type</c> / <c>error_type_desc</c> pair real files an error
    /// under: the describe-specific refusals name their reason, and every
    /// compile error (Msg 156, 207, 208, 11501 …) is <c>SYNTAX</c>.
    /// </summary>
    private static (int Type, string Description) DescribeErrorType(int number) => number switch
    {
        11509 => (3, "CONFLICTING_RESULTS"),
        11521 => (8, "UNDECLARED_PARAMETER"),
        11525 => (10, "TEMPORARY_TABLE"),
        11529 => (1, "MISC"),
        _ => (2, "SYNTAX"),
    };
}
