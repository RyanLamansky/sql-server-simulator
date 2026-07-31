using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly VarcharSqlType DatatypeInfoVarchar32 =
        VarcharSqlType.Get(32, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType DatatypeInfoNVarchar128 =
        NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);

    /// <summary>
    /// Column schema for <c>sp_datatype_info_100</c> — the 20-column ODBC
    /// <c>SQLGetTypeInfo</c> result set (probe-confirmed shape against SQL
    /// Server 2025 via <c>sp_describe_first_result_set</c>). Columns are
    /// left all-nullable (<c>ColumnNullability</c> unset) like the other
    /// fixed-set system procs. The two <c>int</c> columns are <c>PRECISION</c>
    /// (index 2) and <c>NUM_PREC_RADIX</c> (index 17); every other numeric
    /// column is <c>smallint</c>.
    /// </summary>
    private static readonly SqlType[] DatatypeInfoSchema =
    [
        DatatypeInfoNVarchar128, // TYPE_NAME
        SqlType.SmallInt,        // DATA_TYPE
        SqlType.Int32,           // PRECISION
        DatatypeInfoVarchar32,   // LITERAL_PREFIX
        DatatypeInfoVarchar32,   // LITERAL_SUFFIX
        DatatypeInfoVarchar32,   // CREATE_PARAMS
        SqlType.SmallInt,        // NULLABLE
        SqlType.SmallInt,        // CASE_SENSITIVE
        SqlType.SmallInt,        // SEARCHABLE
        SqlType.SmallInt,        // UNSIGNED_ATTRIBUTE
        SqlType.SmallInt,        // MONEY
        SqlType.SmallInt,        // AUTO_INCREMENT
        DatatypeInfoNVarchar128, // LOCAL_TYPE_NAME
        SqlType.SmallInt,        // MINIMUM_SCALE
        SqlType.SmallInt,        // MAXIMUM_SCALE
        SqlType.SmallInt,        // SQL_DATA_TYPE
        SqlType.SmallInt,        // SQL_DATETIME_SUB
        SqlType.Int32,           // NUM_PREC_RADIX
        SqlType.SmallInt,        // INTERVAL_PRECISION
        SqlType.SmallInt,        // USERTYPE
    ];

    private static readonly string[] DatatypeInfoColumnNames =
    [
        "TYPE_NAME", "DATA_TYPE", "PRECISION", "LITERAL_PREFIX", "LITERAL_SUFFIX",
        "CREATE_PARAMS", "NULLABLE", "CASE_SENSITIVE", "SEARCHABLE", "UNSIGNED_ATTRIBUTE",
        "MONEY", "AUTO_INCREMENT", "LOCAL_TYPE_NAME", "MINIMUM_SCALE", "MAXIMUM_SCALE",
        "SQL_DATA_TYPE", "SQL_DATETIME_SUB", "NUM_PREC_RADIX", "INTERVAL_PRECISION", "USERTYPE",
    ];

    // The authoritative datatype-info rows, one raw 20-element array per type,
    // transcribed verbatim from the probe-captured reference data: JSON null =
    // SQL NULL, a string cell = a non-NULL varchar/nvarchar (an empty string
    // stays a non-NULL empty string), a numeric cell = smallint or int per the
    // schema. Two version-tagged sets: ODBC 2.x (concise temporal codes,
    // decimal float/real precision) and ODBC 3.x (verbatim 91/93 temporal codes,
    // binary float/real precision). Both are pre-sorted by the proc's ORDER BY
    // (DATA_TYPE, AUTO_INCREMENT, MONEY, USERTYPE).
    private static readonly object?[][] DatatypeInfoV2Raw =
    [
        ["datetimeoffset", -155, 34, "'", "'", "scale", 1, 0, 3, null, 0, null, "datetimeoffset", 0, 7, -155, 0, null, null, 0],
        ["time", -154, 16, "'", "'", "scale", 1, 0, 3, null, 0, null, "time", 0, 7, -154, 0, null, null, 0],
        ["xml", -152, 0, "N'", "'", null, 1, 1, 0, null, 0, null, "xml", null, null, -152, null, null, null, 0],
        ["sql_variant", -150, 8000, null, null, null, 1, 0, 2, null, 0, null, "sql_variant", 0, 0, -150, null, 10, null, 0],
        ["uniqueidentifier", -11, 36, "'", "'", null, 1, 0, 2, null, 0, null, "uniqueidentifier", null, null, -11, null, null, null, 0],
        ["ntext", -10, 1073741823, "N'", "'", null, 1, 0, 1, null, 0, null, "ntext", null, null, -10, null, null, null, 0],
        ["nvarchar", -9, 4000, "N'", "'", "max length", 1, 0, 3, null, 0, null, "nvarchar", null, null, -9, null, null, null, 0],
        ["sysname", -9, 128, "N'", "'", null, 0, 0, 3, null, 0, null, "sysname", null, null, -9, null, null, null, 18],
        ["nchar", -8, 4000, "N'", "'", "length", 1, 0, 3, null, 0, null, "nchar", null, null, -8, null, null, null, 0],
        ["bit", -7, 1, null, null, null, 1, 0, 2, null, 0, null, "bit", 0, 0, -7, null, null, null, 16],
        ["tinyint", -6, 3, null, null, null, 1, 0, 2, 1, 0, 0, "tinyint", 0, 0, -6, null, 10, null, 5],
        ["tinyint identity", -6, 3, null, null, null, 0, 0, 2, 1, 0, 1, "tinyint identity", 0, 0, -6, null, 10, null, 5],
        ["bigint", -5, 19, null, null, null, 1, 0, 2, 0, 0, 0, "bigint", 0, 0, -5, null, 10, null, 0],
        ["bigint identity", -5, 19, null, null, null, 0, 0, 2, 0, 0, 1, "bigint identity", 0, 0, -5, null, 10, null, 0],
        ["image", -4, 2147483647, "0x", null, null, 1, 0, 0, null, 0, null, "image", null, null, -4, null, null, null, 20],
        ["varbinary", -3, 8000, "0x", null, "max length", 1, 0, 2, null, 0, null, "varbinary", null, null, -3, null, null, null, 4],
        ["binary", -2, 8000, "0x", null, "length", 1, 0, 2, null, 0, null, "binary", null, null, -2, null, null, null, 3],
        ["timestamp", -2, 8, "0x", null, null, 0, 0, 2, null, 0, null, "timestamp", null, null, -2, null, null, null, 80],
        ["text", -1, 2147483647, "'", "'", null, 1, 0, 1, null, 0, null, "text", null, null, -1, null, null, null, 19],
        ["char", 1, 8000, "'", "'", "length", 1, 0, 3, null, 0, null, "char", null, null, 1, null, null, null, 1],
        ["numeric", 2, 38, null, null, "precision,scale", 1, 0, 2, 0, 0, 0, "numeric", 0, 38, 2, null, 10, null, 10],
        ["numeric() identity", 2, 38, null, null, "precision", 0, 0, 2, 0, 0, 1, "numeric() identity", 0, 0, 2, null, 10, null, 10],
        ["decimal", 3, 38, null, null, "precision,scale", 1, 0, 2, 0, 0, 0, "decimal", 0, 38, 3, null, 10, null, 24],
        ["money", 3, 19, "$", null, null, 1, 0, 2, 0, 1, 0, "money", 4, 4, 3, null, 10, null, 11],
        ["smallmoney", 3, 10, "$", null, null, 1, 0, 2, 0, 1, 0, "smallmoney", 4, 4, 3, null, 10, null, 21],
        ["decimal() identity", 3, 38, null, null, "precision", 0, 0, 2, 0, 0, 1, "decimal() identity", 0, 0, 3, null, 10, null, 24],
        ["int", 4, 10, null, null, null, 1, 0, 2, 0, 0, 0, "int", 0, 0, 4, null, 10, null, 7],
        ["int identity", 4, 10, null, null, null, 0, 0, 2, 0, 0, 1, "int identity", 0, 0, 4, null, 10, null, 7],
        ["smallint", 5, 5, null, null, null, 1, 0, 2, 0, 0, 0, "smallint", 0, 0, 5, null, 10, null, 6],
        ["smallint identity", 5, 5, null, null, null, 0, 0, 2, 0, 0, 1, "smallint identity", 0, 0, 5, null, 10, null, 6],
        ["float", 6, 15, null, null, null, 1, 0, 2, 0, 0, 0, "float", null, null, 6, null, 10, null, 8],
        ["real", 7, 7, null, null, null, 1, 0, 2, 0, 0, 0, "real", null, null, 7, null, 10, null, 23],
        ["date", 9, 10, "'", "'", null, 1, 0, 3, null, 0, null, "date", null, 0, 9, 1, null, null, 0],
        ["datetime2", 11, 27, "'", "'", "scale", 1, 0, 3, null, 0, null, "datetime2", 0, 7, 9, 3, null, null, 0],
        ["datetime", 11, 23, "'", "'", null, 1, 0, 3, null, 0, null, "datetime", 3, 3, 9, 3, null, null, 12],
        ["smalldatetime", 11, 16, "'", "'", null, 1, 0, 3, null, 0, null, "smalldatetime", 0, 0, 9, 3, null, null, 22],
        ["varchar", 12, 8000, "'", "'", "max length", 1, 0, 3, null, 0, null, "varchar", null, null, 12, null, null, null, 2],
    ];

    private static readonly object?[][] DatatypeInfoV3Raw =
    [
        ["datetimeoffset", -155, 34, "'", "'", "scale", 1, 0, 3, null, 0, null, "datetimeoffset", 0, 7, -155, 0, null, null, 0],
        ["time", -154, 16, "'", "'", "scale", 1, 0, 3, null, 0, null, "time", 0, 7, -154, 0, null, null, 0],
        ["xml", -152, 0, "N'", "'", null, 1, 1, 0, null, 0, null, "xml", null, null, -152, null, null, null, 0],
        ["sql_variant", -150, 8000, null, null, null, 1, 0, 2, null, 0, null, "sql_variant", 0, 0, -150, null, 10, null, 0],
        ["uniqueidentifier", -11, 36, "'", "'", null, 1, 0, 2, null, 0, null, "uniqueidentifier", null, null, -11, null, null, null, 0],
        ["ntext", -10, 1073741823, "N'", "'", null, 1, 0, 1, null, 0, null, "ntext", null, null, -10, null, null, null, 0],
        ["nvarchar", -9, 4000, "N'", "'", "max length", 1, 0, 3, null, 0, null, "nvarchar", null, null, -9, null, null, null, 0],
        ["sysname", -9, 128, "N'", "'", null, 0, 0, 3, null, 0, null, "sysname", null, null, -9, null, null, null, 18],
        ["nchar", -8, 4000, "N'", "'", "length", 1, 0, 3, null, 0, null, "nchar", null, null, -8, null, null, null, 0],
        ["bit", -7, 1, null, null, null, 1, 0, 2, null, 0, null, "bit", 0, 0, -7, null, null, null, 16],
        ["tinyint", -6, 3, null, null, null, 1, 0, 2, 1, 0, 0, "tinyint", 0, 0, -6, null, 10, null, 5],
        ["tinyint identity", -6, 3, null, null, null, 0, 0, 2, 1, 0, 1, "tinyint identity", 0, 0, -6, null, 10, null, 5],
        ["bigint", -5, 19, null, null, null, 1, 0, 2, 0, 0, 0, "bigint", 0, 0, -5, null, 10, null, 0],
        ["bigint identity", -5, 19, null, null, null, 0, 0, 2, 0, 0, 1, "bigint identity", 0, 0, -5, null, 10, null, 0],
        ["image", -4, 2147483647, "0x", null, null, 1, 0, 0, null, 0, null, "image", null, null, -4, null, null, null, 20],
        ["varbinary", -3, 8000, "0x", null, "max length", 1, 0, 2, null, 0, null, "varbinary", null, null, -3, null, null, null, 4],
        ["binary", -2, 8000, "0x", null, "length", 1, 0, 2, null, 0, null, "binary", null, null, -2, null, null, null, 3],
        ["timestamp", -2, 8, "0x", null, null, 0, 0, 2, null, 0, null, "timestamp", null, null, -2, null, null, null, 80],
        ["text", -1, 2147483647, "'", "'", null, 1, 0, 1, null, 0, null, "text", null, null, -1, null, null, null, 19],
        ["char", 1, 8000, "'", "'", "length", 1, 0, 3, null, 0, null, "char", null, null, 1, null, null, null, 1],
        ["numeric", 2, 38, null, null, "precision,scale", 1, 0, 2, 0, 0, 0, "numeric", 0, 38, 2, null, 10, null, 10],
        ["numeric() identity", 2, 38, null, null, "precision", 0, 0, 2, 0, 0, 1, "numeric() identity", 0, 0, 2, null, 10, null, 10],
        ["decimal", 3, 38, null, null, "precision,scale", 1, 0, 2, 0, 0, 0, "decimal", 0, 38, 3, null, 10, null, 24],
        ["money", 3, 19, "$", null, null, 1, 0, 2, 0, 1, 0, "money", 4, 4, 3, null, 10, null, 11],
        ["smallmoney", 3, 10, "$", null, null, 1, 0, 2, 0, 1, 0, "smallmoney", 4, 4, 3, null, 10, null, 21],
        ["decimal() identity", 3, 38, null, null, "precision", 0, 0, 2, 0, 0, 1, "decimal() identity", 0, 0, 3, null, 10, null, 24],
        ["int", 4, 10, null, null, null, 1, 0, 2, 0, 0, 0, "int", 0, 0, 4, null, 10, null, 7],
        ["int identity", 4, 10, null, null, null, 0, 0, 2, 0, 0, 1, "int identity", 0, 0, 4, null, 10, null, 7],
        ["smallint", 5, 5, null, null, null, 1, 0, 2, 0, 0, 0, "smallint", 0, 0, 5, null, 10, null, 6],
        ["smallint identity", 5, 5, null, null, null, 0, 0, 2, 0, 0, 1, "smallint identity", 0, 0, 5, null, 10, null, 6],
        ["float", 6, 53, null, null, null, 1, 0, 2, 0, 0, 0, "float", null, null, 6, null, 2, null, 8],
        ["real", 7, 24, null, null, null, 1, 0, 2, 0, 0, 0, "real", null, null, 7, null, 2, null, 23],
        ["varchar", 12, 8000, "'", "'", "max length", 1, 0, 3, null, 0, null, "varchar", null, null, 12, null, null, null, 2],
        ["date", 91, 10, "'", "'", null, 1, 0, 3, null, 0, null, "date", null, 0, 9, 1, null, null, 0],
        ["datetime2", 93, 27, "'", "'", "scale", 1, 0, 3, null, 0, null, "datetime2", 0, 7, 9, 3, null, null, 0],
        ["datetime", 93, 23, "'", "'", null, 1, 0, 3, null, 0, null, "datetime", 3, 3, 9, 3, null, null, 12],
        ["smalldatetime", 93, 16, "'", "'", null, 1, 0, 3, null, 0, null, "smalldatetime", 0, 0, 9, 3, null, null, 22],
    ];

    private static readonly SqlValue[][] DatatypeInfoV2Rows = BuildDatatypeInfoRows(DatatypeInfoV2Raw);

    private static readonly SqlValue[][] DatatypeInfoV3Rows = BuildDatatypeInfoRows(DatatypeInfoV3Raw);

    private static SqlValue[][] BuildDatatypeInfoRows(object?[][] raw)
    {
        var rows = new SqlValue[raw.Length][];
        for (var i = 0; i < raw.Length; i++)
            rows[i] = BuildDatatypeInfoRow(raw[i]);
        return rows;
    }

    private static SqlValue[] BuildDatatypeInfoRow(object?[] raw)
    {
        var row = new SqlValue[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var type = DatatypeInfoSchema[i];
            row[i] = raw[i] switch
            {
                null => SqlValue.Null(type),
                string text => SqlValue.FromString(type, text),
                int number => type == SqlType.SmallInt ? SqlValue.FromInt16((short)number) : SqlValue.FromInt32(number),
                _ => throw new InvalidOperationException($"Unsupported datatype-info cell {raw[i]}."),
            };
        }

        return row;
    }

    /// <summary>
    /// Handles <c>EXEC sp_datatype_info_100 [@data_type] [, @ODBCVer]</c> — the
    /// proc ODBC's <c>SQLGetTypeInfo</c> calls on connect to learn each type's
    /// precision/scale (also reached via the <c>sys.sp_datatype_info_100</c>
    /// name-form RPC through a synthesized EXEC). Mirrors the real proc's
    /// semantics: <c>@data_type</c> (positional or named, NULL/absent → 0)
    /// selects a single <c>DATA_TYPE</c> when non-zero or every type when 0;
    /// <c>@ODBCVer</c> (positional or named, absent → 2) collapses to 2 (values
    /// &lt; 3) or 3, choosing the version-tagged row set — the split drives the
    /// temporal <c>DATA_TYPE</c> codes (e.g. <c>datetime2</c> = 11 in v2, 93 in
    /// v3) and float/real precision + radix. Rows are filtered by <c>DATA_TYPE</c>
    /// in range and sorted by (<c>DATA_TYPE</c>, <c>AUTO_INCREMENT</c>,
    /// <c>MONEY</c>, <c>USERTYPE</c>) with NULL <c>AUTO_INCREMENT</c> first.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDatatypeInfo100(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (dataType, odbcVer) = ParseDatatypeInfoArgs(arguments);
        var source = odbcVer >= 3 ? DatatypeInfoV3Rows : DatatypeInfoV2Rows;
        var (low, high) = dataType == 0 ? (-32768, 32767) : (dataType, dataType);

        var rows = source
            .Where(row => row[1].AsInt16 >= low && row[1].AsInt16 <= high)
            .OrderBy(row => (int)row[1].AsInt16)
            .ThenBy(DatatypeInfoOrderKey12)
            .ThenBy(row => (int)row[10].AsInt16)
            .ThenBy(row => (int)row[19].AsInt16)
            .ToList();

        yield return new SimulatedSqlResultSet(DatatypeInfoSchema, DatatypeInfoColumnNames, rows);
    }

    // AUTO_INCREMENT (column index 11) sort key: NULL sorts first, matching
    // ORDER BY ascending, so a NULL cell maps to int.MinValue.
    private static int DatatypeInfoOrderKey12(SqlValue[] row) => row[11].IsNull ? int.MinValue : row[11].AsInt16;

    private static (int DataType, int OdbcVer) ParseDatatypeInfoArgs(List<ProcArgument> arguments)
    {
        var dataType = 0;
        var odbcVer = 2;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: dataType = DatatypeInfoArgValue(arg, 0, SqlType.Int32); break;
                    case 1: odbcVer = DatatypeInfoArgValue(arg, 2, SqlType.TinyInt); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_datatype_info_100");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "data_type"): dataType = DatatypeInfoArgValue(arg, 0, SqlType.Int32); break;
                case var n when BuiltInToken.Equals(n, "ODBCVer"): odbcVer = DatatypeInfoArgValue(arg, 2, SqlType.TinyInt); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_datatype_info_100");
            }
        }

        return (dataType, odbcVer < 3 ? 2 : 3);
    }

    private static int DatatypeInfoArgValue(ProcArgument arg, int fallback, SqlType target) =>
        arg.IsDefault || arg.Value.IsNull ? fallback : ScalarArguments.CoerceProcedureParameter(arg.Value, target);
}
