using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// How many arguments each built-in accepts, and the error real raises for a
/// call outside that count: Msg 174 for a fixed count, Msg 189 for a range,
/// Msg 1076 for an open minimum — class 15, so real refuses the call while it
/// parses, before any argument binds. A function's own parser reads its
/// argument list by position and would otherwise meet the stray comma or
/// parenthesis as a Msg 102. Every rule is real's, read off a sweep of each
/// built-in over zero to four arguments (probed 2026-09-26 against SQL Server
/// 2025), wording and state included — below and above the accepted count can
/// be refused differently (<c>OBJECT_DEFINITION</c>, <c>TRIM</c>). A built-in
/// with its own argument grammar — <c>CAST</c>'s <c>AS</c>, <c>JSON_OBJECT</c>'s
/// key-value pairs, a datepart, an <c>OVER</c>-bound window function — has no
/// rule here, since a comma count doesn't describe its call.
/// </summary>
internal readonly struct BuiltInArity(int min, int max, BuiltInArity.Refusal belowMin, BuiltInArity.Refusal aboveMax, bool emptyListParses = false)
{
    /// <summary>
    /// One of the three arity errors, as real words it for one function.
    /// </summary>
    internal readonly struct Refusal(short number, string name, int first, int second, byte state)
    {
        public SimulatedSqlException ToException() => number switch
        {
            174 => SimulatedSqlException.FunctionRequiresNArguments(name, first, state),
            189 => SimulatedSqlException.FunctionArgumentCountRange(name, first, second, state),
            _ => SimulatedSqlException.FunctionRequiresAtLeastNArguments(name, first),
        };
    }

    private static BuiltInArity Exactly(string name, int count, bool emptyListParses = false)
    {
        var refusal = new Refusal(174, name, count, 0, 1);
        return new(count, count, refusal, refusal, emptyListParses);
    }

    private static BuiltInArity Between(string name, int min, int max)
    {
        var refusal = new Refusal(189, name, min, max, 1);
        return new(min, max, refusal, refusal);
    }

    private static BuiltInArity AtLeast(string name, int min) =>
        new(min, int.MaxValue, new Refusal(1076, name, min, 0, 1), default);

    /// <summary>
    /// Refuses the call whose argument list starts at the cursor — the token
    /// after its opening parenthesis — when its argument count is outside what
    /// <paramref name="uppercaseName"/> accepts, leaving the cursor where it
    /// was either way. An empty list some built-ins leave to their own parser,
    /// where real's refusal is a Msg 102 at the closing parenthesis
    /// (<c>LEFT()</c>).
    /// </summary>
    public static void Check(ReadOnlySpan<char> uppercaseName, ParserContext context)
    {
        if (For(uppercaseName) is not { } arity)
            return;

        var checkpoint = context.SaveCheckpoint();
        var count = CountArguments(context);
        context.RestoreCheckpoint(checkpoint);
        if (count < 0 || (count == 0 && arity.emptyListParses))
            return;
        if (count < arity.min)
            throw arity.belowMin.ToException();
        if (count > arity.max)
            throw arity.aboveMax.ToException();
    }

    /// <summary>
    /// Counts the top-level arguments from the cursor to the closing
    /// parenthesis, or answers -1 for a list whose own syntax error real
    /// reports ahead of its count: an unclosed list, an empty argument
    /// (<c>ISNULL(1, 2,)</c>), an aggregate's <c>DISTINCT</c> / <c>ALL</c>,
    /// which admits one expression (<c>COUNT(DISTINCT 1, 2)</c>), or a
    /// top-level <c>FROM</c>, which <c>TRIM</c> reads as its own grammar and
    /// any other built-in's parser refuses (<c>SUBSTRING(s FROM 1)</c>).
    /// </summary>
    private static int CountArguments(ParserContext context)
    {
        var count = 0;
        var depth = 0;
        var expectArgument = true;
        do
        {
            switch (context.Token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' } when depth == 0:
                    return expectArgument && count > 0 ? -1 : count;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: ',' } when depth == 0:
                    if (expectArgument)
                        return -1;
                    expectArgument = true;
                    continue;
                case ReservedKeyword { Keyword: Keyword.All or Keyword.Distinct or Keyword.From } when depth == 0:
                    return -1;
            }
            if (expectArgument)
            {
                count++;
                expectArgument = false;
            }
        }
        while (context.MoveNext());
        return -1;
    }

    private readonly int min = min;
    private readonly int max = max;
    private readonly Refusal belowMin = belowMin;
    private readonly Refusal aboveMax = aboveMax;
    private readonly bool emptyListParses = emptyListParses;

    private static BuiltInArity? For(ReadOnlySpan<char> uppercaseName) =>
        uppercaseName switch
        {
            "ABS" => Exactly("abs", 1),
            "ACOS" => Exactly("acos", 1),
            "APPLOCK_MODE" => Between("applock_mode", 2, 3),
            "APPLOCK_TEST" => Between("applock_test", 3, 4),
            "APPROX_COUNT_DISTINCT" => Exactly("approx_count_distinct", 1),
            "APP_NAME" => Exactly("app_name", 0),
            "ASCII" => Exactly("ascii", 1),
            "ASIN" => Exactly("asin", 1),
            "ASSEMBLYPROPERTY" => Exactly("assemblyproperty", 2),
            "ATAN" => Exactly("atan", 1),
            "ATN2" => Exactly("atn2", 2),
            "AVG" => Exactly("avg", 1),
            "BASE64_DECODE" => Exactly("base64_decode", 1),
            "BASE64_ENCODE" => Between("base64_encode", 1, 2),
            "BINARY_CHECKSUM" => AtLeast("binary_checksum", 1),
            "BIT_COUNT" => Exactly("bit_count", 1),
            "CEILING" => Exactly("ceiling", 1),
            "CERTENCODED" => Exactly("CertEncoded", 1),
            "CERTPRIVATEKEY" => Between("CertPrivateKey", 2, 3),
            "CHAR" => Exactly("char", 1),
            "CHARINDEX" => Between("charindex", 2, 3),
            "CHECKSUM" => AtLeast("checksum", 1),
            "CHECKSUM_AGG" => Exactly("checksum_agg", 1),
            "CHOOSE" => AtLeast("choose", 2),
            "COLLATIONPROPERTY" => Exactly("collationproperty", 2),
            "COLUMNPROPERTY" => Exactly("columnproperty", 3),
            "COLUMNS_UPDATED" => Exactly("columns_updated", 0),
            "COL_LENGTH" => Exactly("col_length", 2),
            "COL_NAME" => Exactly("col_name", 2),
            "COMPRESS" => Exactly("Compress", 1),
            "CONCAT" => Between("concat", 2, 254),
            "CONCAT_WS" => Between("concat_ws", 3, 254),
            "CONNECTIONPROPERTY" => Exactly("connectionproperty", 1),
            "CONTEXT_INFO" => Exactly("context_info", 0),
            "COS" => Exactly("cos", 1),
            "COT" => Exactly("cot", 1),
            "COUNT" => Exactly("count", 1),
            "COUNT_BIG" => Exactly("count_big", 1),
            "CRYPT_GEN_RANDOM" => Between("Crypt_Gen_Random", 1, 2),
            "CURRENT_REQUEST_ID" => Exactly("current_request_id", 0),
            "CURRENT_TRANSACTION_ID" => Exactly("current_transaction_id", 0),
            "CURSOR_STATUS" => Exactly("cursor_status", 2),
            "DATABASEPROPERTYEX" => Exactly("databasepropertyex", 2),
            "DATABASE_PRINCIPAL_ID" => Between("database_principal_id", 0, 1),
            "DATALENGTH" => Exactly("datalength", 1),
            "DATEADD" => Exactly("dateadd", 3),
            "DATEDIFF" => Exactly("datediff", 3),
            "DATEDIFF_BIG" => Exactly("datediff_big", 3),
            "DATEFROMPARTS" => Exactly("datefromparts", 3),
            "DATENAME" => Exactly("datename", 2),
            "DATEPART" => Exactly("datepart", 2),
            "DATETIME2FROMPARTS" => Exactly("datetime2fromparts", 8),
            "DATETIMEFROMPARTS" => Exactly("datetimefromparts", 7),
            "DATETIMEOFFSETFROMPARTS" => Exactly("datetimeoffsetfromparts", 10),
            "DATETRUNC" => Exactly("datetrunc", 2),
            "DATE_BUCKET" => Between("Date_Bucket", 3, 4),
            "DAY" => Exactly("day", 1),
            "DB_ID" => Between("db_id", 0, 1),
            "DB_NAME" => Between("db_name", 0, 1),
            "DECOMPRESS" => Exactly("Decompress", 1),
            "DEGREES" => Exactly("degrees", 1),
            "DIFFERENCE" => Exactly("difference", 2),
            "EOMONTH" => Between("eomonth", 1, 2),
            "ERROR_LINE" => Exactly("error_line", 0),
            "ERROR_MESSAGE" => Exactly("error_message", 0),
            "ERROR_NUMBER" => Exactly("error_number", 0),
            "ERROR_PROCEDURE" => Exactly("error_procedure", 0),
            "ERROR_SEVERITY" => Exactly("error_severity", 0),
            "ERROR_STATE" => Exactly("error_state", 0),
            "EVENTDATA" => Exactly("EventData", 0),
            "EXP" => Exactly("exp", 1),
            "FILEGROUPPROPERTY" => Exactly("filegroupproperty", 2),
            "FILEGROUP_ID" => Exactly("filegroup_id", 1),
            "FILEGROUP_NAME" => Exactly("filegroup_name", 1),
            "FILEPROPERTY" => Exactly("fileproperty", 2),
            "FILE_ID" => Exactly("file_id", 1),
            "FILE_IDEX" => Exactly("file_idex", 1),
            "FILE_NAME" => Exactly("file_name", 1),
            "FLOOR" => Exactly("floor", 1),
            "FORMAT" => Between("format", 2, 3),
            "FORMATMESSAGE" => Between("formatmessage", 1, 21),
            "FULLTEXTCATALOGPROPERTY" => Exactly("fulltextcatalogproperty", 2),
            "FULLTEXTSERVICEPROPERTY" => Exactly("fulltextserviceproperty", 1),
            "GETANSINULL" => Between("getansinull", 0, 1),
            "GETDATE" => Exactly("getdate", 0),
            "GETUTCDATE" => Exactly("getutcdate", 0),
            "GET_BIT" => Exactly("get_bit", 2),
            "GET_FILESTREAM_TRANSACTION_CONTEXT" => Exactly("get_filestream_transaction_context", 0),
            "GREATEST" => Between("greatest", 1, 254),
            "GROUPING" => Exactly("grouping", 1),
            "HASHBYTES" => Exactly("hashbytes", 2),
            "HAS_DBACCESS" => Exactly("has_dbaccess", 1),
            "HAS_PERMS_BY_NAME" => Between("has_perms_by_name", 3, 5),
            "HOST_NAME" => Exactly("host_name", 0),
            "IDENT_CURRENT" => Exactly("ident_current", 1),
            "IDENT_INCR" => Exactly("ident_incr", 1),
            "IDENT_SEED" => Exactly("ident_seed", 1),
            "INDEXKEY_PROPERTY" => Exactly("indexkey_property", 4),
            "INDEXPROPERTY" => Exactly("indexproperty", 3),
            "INDEX_COL" => Exactly("index_col", 3),
            "ISDATE" => Exactly("isdate", 1),
            "ISJSON" => Between("isjson", 1, 2),
            "ISNULL" => Exactly("isnull", 2),
            "ISNUMERIC" => Exactly("isnumeric", 1),
            "IS_MEMBER" => Exactly("is_member", 1),
            "IS_ROLEMEMBER" => Between("is_rolemember", 1, 2),
            "IS_SRVROLEMEMBER" => Between("is_srvrolemember", 1, 2),
            "JSON_ARRAYAGG" => new(1, 1, new(174, "json_arrayagg", 1, 0, 1), new(174, "json_arrayagg", 1, 0, 3)),
            "JSON_MODIFY" => Exactly("json_modify", 3),
            "JSON_PATH_EXISTS" => Exactly("json_path_exists", 2),
            "JSON_QUERY" => new(1, 2, new(189, "json_query", 1, 2, 2), new(189, "json_query", 1, 2, 3)),
            "JSON_VALUE" => new(2, 2, new(174, "json_value", 2, 0, 2), new(174, "json_value", 2, 0, 3)),
            "LEAST" => Between("least", 1, 254),
            "LEFT" => Exactly("left", 2, emptyListParses: true),
            "LEFT_SHIFT" => Exactly("left_shift", 2),
            "LEN" => Exactly("len", 1),
            "LOG" => Between("log", 1, 2),
            "LOG10" => Exactly("log10", 1),
            "LOGINPROPERTY" => Exactly("LoginProperty", 2),
            "LOWER" => Exactly("lower", 1),
            "LTRIM" => Between("ltrim", 1, 2),
            "MAX" => Exactly("max", 1),
            "MIN" => Exactly("min", 1),
            "MIN_ACTIVE_ROWVERSION" => Exactly("min_active_rowversion", 0),
            "MONTH" => Exactly("month", 1),
            "NCHAR" => Exactly("nchar", 1),
            "NEWID" => Exactly("newid", 0),
            "NEWSEQUENTIALID" => Exactly("newsequentialid", 0),
            "OBJECTPROPERTY" => Exactly("objectproperty", 2),
            "OBJECTPROPERTYEX" => Exactly("objectpropertyex", 2),
            "OBJECT_DEFINITION" => new(1, 2, new(189, "object_definition", 1, 3, 1), new(174, "object_definition", 1, 0, 5)),
            "OBJECT_ID" => Between("object_id", 1, 2),
            "OBJECT_NAME" => Between("object_name", 1, 2),
            "OBJECT_SCHEMA_NAME" => Between("object_schema_name", 1, 2),
            "ORIGINAL_DB_NAME" => Exactly("original_db_name", 0),
            "ORIGINAL_LOGIN" => Exactly("original_login", 0),
            "PARSENAME" => Exactly("parsename", 2),
            "PATINDEX" => Exactly("patindex", 2),
            "PERCENTILE_CONT" => Exactly("percentile_cont", 1),
            "PERCENTILE_DISC" => Exactly("percentile_disc", 1),
            "PERMISSIONS" => Between("permissions", 0, 2),
            "PI" => Exactly("pi", 0),
            "POWER" => Exactly("power", 2),
            "PRODUCT" => Exactly("product", 1),
            "PWDCOMPARE" => Between("pwdcompare", 2, 3),
            "PWDENCRYPT" => Exactly("pwdencrypt", 1),
            "QUOTENAME" => Between("quotename", 1, 2),
            "RADIANS" => Exactly("radians", 1),
            "RAND" => Between("rand", 0, 1),
            "REGEXP_COUNT" => Between("regexp_count", 2, 4),
            "REGEXP_INSTR" => Between("regexp_instr", 2, 7),
            "REGEXP_REPLACE" => Between("regexp_replace", 2, 6),
            "REGEXP_SUBSTR" => Between("regexp_substr", 2, 6),
            "REPLACE" => Exactly("replace", 3),
            "REPLICATE" => Exactly("replicate", 2),
            "REVERSE" => Exactly("reverse", 1),
            "RIGHT" => Exactly("right", 2, emptyListParses: true),
            "RIGHT_SHIFT" => Exactly("right_shift", 2),
            "ROUND" => Between("round", 2, 3),
            "ROWCOUNT_BIG" => Exactly("rowcount_big", 0),
            "RTRIM" => Between("rtrim", 1, 2),
            "SCHEMA_ID" => Between("schema_id", 0, 1),
            "SCHEMA_NAME" => Between("schema_name", 0, 1),
            "SCOPE_IDENTITY" => Exactly("scope_identity", 0),
            "SERVERPROPERTY" => Exactly("serverproperty", 1),
            "SESSIONPROPERTY" => Exactly("sessionproperty", 1),
            "SESSION_CONTEXT" => Exactly("session_context", 1),
            "SET_BIT" => Between("set_bit", 2, 3),
            "SID_BINARY" => Exactly("sid_binary", 1),
            "SIGN" => Exactly("sign", 1),
            "SIN" => Exactly("sin", 1),
            "SMALLDATETIMEFROMPARTS" => Exactly("smalldatetimefromparts", 5),
            "SOUNDEX" => Exactly("soundex", 1),
            "SPACE" => Exactly("space", 1),
            "SQL_VARIANT_PROPERTY" => Exactly("sql_variant_property", 2),
            "SQRT" => Exactly("sqrt", 1),
            "SQUARE" => Exactly("square", 1),
            "STATS_DATE" => Exactly("stats_date", 2),
            "STDEV" => Exactly("stdev", 1),
            "STDEVP" => Exactly("stdevp", 1),
            "STR" => Between("str", 1, 3),
            "STRING_AGG" => Exactly("string_agg", 2),
            "STRING_ESCAPE" => Exactly("string_escape", 2),
            "STUFF" => Exactly("stuff", 4),
            "SUBSTRING" => Between("substring", 2, 3),
            "SUM" => Exactly("sum", 1),
            "SUSER_ID" => Between("suser_id", 0, 1),
            "SUSER_NAME" => Between("suser_name", 0, 1),
            "SUSER_SID" => Between("suser_sid", 0, 2),
            "SUSER_SNAME" => Between("suser_sname", 0, 1),
            "SWITCHOFFSET" => Exactly("switchoffset", 2),
            "SYSDATETIME" => Exactly("sysdatetime", 0),
            "SYSDATETIMEOFFSET" => Exactly("sysdatetimeoffset", 0),
            "SYSUTCDATETIME" => Exactly("sysutcdatetime", 0),
            "TAN" => Exactly("tan", 1),
            "TEXTPTR" => Exactly("textptr", 1),
            "TEXTVALID" => Exactly("textvalid", 2),
            "TIMEFROMPARTS" => Exactly("timefromparts", 5),
            "TODATETIMEOFFSET" => Exactly("todatetimeoffset", 2),
            "TRANSLATE" => Exactly("translate", 3),
            "TRIGGER_NESTLEVEL" => Between("Trigger_Nestlevel", 0, 3),
            "TRIM" => new(1, 1, new(189, "Trim", 1, 3, 1), new(174, "trim", 1, 0, 1)),
            "TYPEPROPERTY" => Exactly("typeproperty", 2),
            "TYPE_ID" => Exactly("type_id", 1),
            "TYPE_NAME" => Exactly("type_name", 1),
            "UNICODE" => Exactly("unicode", 1),
            "UNISTR" => Between("unistr", 1, 2),
            "UPPER" => Exactly("upper", 1),
            "USER_ID" => Between("user_id", 0, 1),
            "USER_NAME" => Between("user_name", 0, 1),
            "VAR" => Exactly("var", 1),
            "VARP" => Exactly("varp", 1),
            "XACT_STATE" => Exactly("xact_state", 0),
            "XML_SCHEMA_NAMESPACE" => Between("XML_SCHEMA_NAMESPACE", 2, 3),
            "YEAR" => Exactly("year", 1),
            _ => null,
        };
}
