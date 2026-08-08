using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared formatter for <see cref="JsonObject"/> and <see cref="JsonArray"/>
/// values. Renders a <see cref="SqlValue"/> as a JSON fragment matching real
/// SQL Server's JSON_OBJECT / JSON_ARRAY output (probe-confirmed against
/// SQL Server 2025 — 2026-05-23). Numbers / booleans emit unquoted JSON
/// primitives; varbinary emits base64-quoted; datetime2 / smalldatetime use
/// the <c>T</c>-separated ISO form (date / time keep their default ISO
/// strings); other strings (including <c>uniqueidentifier</c>) go through
/// the standard JSON string-escape path. <c>float</c> / <c>real</c> take
/// CONVERT style 126 — the source-precision scientific form every JSON
/// producer writes (16 significant digits for float, 8 for real), where a
/// styleless conversion to a string type would write style 0's compact form.
/// </summary>
internal static class JsonValueRender
{
    /// <summary>JSON null token shared across builders.</summary>
    public const string NullLiteral = "null";

    /// <summary>
    /// Renders <paramref name="value"/> as a JSON fragment, appending the
    /// result onto <paramref name="sb"/>. When <paramref name="embedRaw"/>
    /// is true, the value's string form is appended verbatim (used for
    /// JSON-producing inputs such as nested <c>JSON_OBJECT</c> /
    /// <c>JSON_ARRAY</c> / <c>JSON_QUERY</c> — matches SQL Server's
    /// auto-detection of JSON-typed input). A string value carries the
    /// <c>\/</c> solidus escape every one of these producers writes.
    /// </summary>
    public static void Append(StringBuilder sb, SqlValue value, bool embedRaw)
    {
        if (value.IsNull)
        {
            _ = sb.Append(NullLiteral);
            return;
        }
        if (embedRaw)
        {
            _ = sb.Append(value.CoerceTo(SqlType.NVarchar).AsString);
            return;
        }

        var type = value.Type;
        if (type == SqlType.Bit)
        {
            _ = sb.Append(value.AsBoolean ? "true" : "false");
            return;
        }
        if (SqlType.IsIntegerCategory(type))
        {
            _ = sb.Append(IntegerAsString(type, value));
            return;
        }
        if (type is DecimalSqlType)
        {
            _ = sb.Append(value.AsDecimal38.ToString());
            return;
        }
        if (SqlType.IsMoneyCategory(type))
        {
            // money / smallmoney serialize as decimal numbers (4 decimal
            // places preserved by .ToString on the underlying decimal).
            _ = sb.Append(value.AsMoneyDecimal38.ToString());
            return;
        }
        if (type == SqlType.Float || type == SqlType.Real)
        {
            _ = sb.Append(value.FormatApproximateWithStyle(126));
            return;
        }
        if (type is BinarySqlType or VarbinarySqlType)
        {
            _ = sb.Append('"').Append(Convert.ToBase64String(value.AsBytes)).Append('"');
            return;
        }
        if (type is DateTime2SqlType dt2)
        {
            AppendIsoDateTime(sb, value.AsDateTime2, dt2.precision);
            return;
        }
        if (type == SqlType.DateTime)
        {
            AppendIsoDateTime(sb, value.AsDateTime, 3);
            return;
        }
        if (type == SqlType.SmallDateTime)
        {
            AppendIsoDateTime(sb, value.AsSmallDateTime, 0);
            return;
        }

        // Date / Time / Uniqueidentifier / string types / sql_variant /
        // everything else: coerce to nvarchar (SQL Server's default ISO
        // shape for the temporal types and uppercase-hex form for guid),
        // then JSON-escape.
        AppendJsonString(sb, value.CoerceTo(SqlType.NVarchar).AsString, escapeSolidus: true);
    }

    /// <summary>
    /// Coerces a key SqlValue into a quoted, escaped JSON property name.
    /// Numeric keys cast to their string form; NULL keys raise Msg 13601.
    /// A key escapes <c>/</c> the same way a value does (probe-confirmed:
    /// <c>JSON_OBJECT('k/1': 'v')</c> is <c>{"k\/1":"v"}</c>).
    /// </summary>
    public static void AppendKey(StringBuilder sb, SqlValue keyValue)
    {
        if (keyValue.IsNull)
            throw SimulatedSqlException.JsonObjectNullKey();
        AppendJsonString(sb, keyValue.CoerceTo(SqlType.NVarchar).AsString, escapeSolidus: true);
    }

    private static void AppendIsoDateTime(StringBuilder sb, DateTime dt, int precision)
    {
        var format = precision == 0
            ? "yyyy-MM-ddTHH:mm:ss"
            : "yyyy-MM-ddTHH:mm:ss." + new string('f', precision);
        _ = sb.Append('"').Append(dt.ToString(format, CultureInfo.InvariantCulture)).Append('"');
    }

    private static string IntegerAsString(SqlType type, SqlValue value)
    {
        var culture = CultureInfo.InvariantCulture;
        return type == SqlType.TinyInt ? value.AsByte.ToString(culture)
            : type == SqlType.SmallInt ? value.AsInt16.ToString(culture)
            : type == SqlType.Int32 ? value.AsInt32.ToString(culture)
            : value.AsInt64.ToString(culture);
    }

    /// <summary>
    /// Appends <paramref name="s"/> as a quoted JSON string.
    /// <paramref name="escapeSolidus"/> is the one per-caller choice: every
    /// JSON producer writes <c>\/</c> for <c>/</c>, while the two sites that
    /// reach this helper directly leave the character literal — the
    /// <c>REGEXP_MATCHES</c> rowset member's <c>substring_matches</c> column
    /// and the property name <c>JSON_MODIFY</c> takes from its path's own
    /// text (both probe-confirmed).
    /// </summary>
    public static void AppendJsonString(StringBuilder sb, string s, bool escapeSolidus = false)
    {
        // Minimal JSON-string escape: only the chars JSON syntax requires
        // (probe-confirmed against SQL Server 2025 — non-ASCII / `<` / `>`
        // are left literal).
        _ = sb.Append('"');
        foreach (var c in s)
        {
            _ = c switch
            {
                '"' => sb.Append("\\\""),
                '/' when escapeSolidus => sb.Append("\\/"),
                '\\' => sb.Append("\\\\"),
                '\b' => sb.Append("\\b"),
                '\f' => sb.Append("\\f"),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                '\t' => sb.Append("\\t"),
                _ when c < 0x20 => sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)),
                _ => sb.Append(c),
            };
        }
        _ = sb.Append('"');
    }

    /// <summary>
    /// Returns true when an Expression produces a JSON document whose
    /// string form should be embedded verbatim (not re-escaped) when used
    /// as a value inside <c>JSON_OBJECT</c> / <c>JSON_ARRAY</c> or as
    /// <c>JSON_MODIFY</c>'s substituted value.
    /// Compile-time check on the Expression's runtime shape — matches SQL
    /// Server's "input is JSON-typed" detection without needing an
    /// SqlValue-level marker bit. Parenthesized wrappers unwrap so
    /// <c>(json_query(...))</c> still flags as raw.
    /// </summary>
    public static bool ProducesJson(Expression expression) => expression switch
    {
        JsonObject or JsonArray or JsonQuery or JsonModify => true,
        Parenthesized p => ProducesJson(p.Wrapped),
        _ => false,
    };
}

/// <summary>
/// Either-or clause that follows a JSON builder's value list, controlling
/// whether SQL NULL value-expressions appear in the output as JSON null or are
/// absent altogether. The default is builder-specific (probe-confirmed against
/// SQL Server 2025): the array builders (<c>JSON_ARRAY</c> / <c>JSON_ARRAYAGG</c>)
/// default to <see cref="AbsentOnNull"/>, while the object builders
/// (<c>JSON_OBJECT</c> / <c>JSON_OBJECTAGG</c>) default to <see cref="NullOnNull"/>.
/// </summary>
internal enum JsonNullClause
{
    /// <summary>NULL value-expressions are omitted from the output.</summary>
    AbsentOnNull,
    /// <summary>NULL value-expressions emit a JSON <c>null</c>.</summary>
    NullOnNull,
}

internal static class JsonNullClauseParser
{
    /// <summary>
    /// Consumes an optional <c>ABSENT ON NULL</c> / <c>NULL ON NULL</c>
    /// suffix from the current cursor position. Both keywords are
    /// contextual identifiers (<c>NULL</c> is the reserved literal, the
    /// rest are bare names). Leaves the cursor on the last consumed token
    /// (matching the standard parser-context contract). Returns the
    /// resolved clause; the default when neither shape appears is
    /// <see cref="JsonNullClause.AbsentOnNull"/>.
    /// </summary>
    public static JsonNullClause Parse(ParserContext context) =>
        Parse(context, JsonNullClause.AbsentOnNull);

    /// <summary>
    /// As <see cref="Parse(ParserContext)"/>, but returns
    /// <paramref name="default"/> when no explicit clause is present. The
    /// aggregate builders supply their kind-specific default
    /// (<c>JSON_ARRAYAGG</c> → <see cref="JsonNullClause.AbsentOnNull"/>,
    /// <c>JSON_OBJECTAGG</c> → <see cref="JsonNullClause.NullOnNull"/>).
    /// </summary>
    public static JsonNullClause Parse(ParserContext context, JsonNullClause @default)
    {
        // ABSENT ON NULL — bare `absent` Name token followed by ON NULL.
        if (context.Token is UnquotedString { Value: var absentText }
            && Collation.Baseline.Equals(absentText, "absent"))
        {
            ExpectOnNull(context);
            return JsonNullClause.AbsentOnNull;
        }
        // NULL ON NULL — leading NULL keyword.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Null })
        {
            ExpectOnNull(context);
            return JsonNullClause.NullOnNull;
        }
        return @default;
    }

    private static void ExpectOnNull(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Null })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
    }
}

/// <summary>
/// SQL <c>JSON_OBJECT([key1 : value1 [, ... keyN : valueN]] [null_clause])</c>:
/// builds a JSON object string from key-value pairs. Default null clause is
/// <see cref="JsonNullClause.NullOnNull"/> — NULL values emit a JSON
/// <c>null</c> (Microsoft documents this default verbatim; note it is the
/// opposite of both <c>JSON_ARRAY</c> and the <c>FOR JSON</c> clause, which
/// omit NULLs by default). Duplicate keys are preserved verbatim (matching
/// real SQL Server — no dedup). Result type is <see cref="SqlType.NVarcharMax"/> (<c>nvarchar(max)</c>).
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-23): empty argument
/// list yields <c>{}</c>; nested <c>JSON_OBJECT</c> / <c>JSON_ARRAY</c> /
/// <c>JSON_QUERY</c> values embed as raw JSON (not re-quoted); other
/// strings go through JSON's escape set (<c>\"</c>, <c>\\</c>, control
/// chars). NULL key raises Msg 13638 at runtime; missing <c>:</c> /
/// trailing comma / partial null-clause raise Msg 102 at parse.
/// </remarks>
internal sealed class JsonObject : Expression
{
    private readonly (Expression Key, Expression Value, bool EmbedRaw)[] entries;
    private readonly JsonNullClause nullClause;

    public JsonObject(ParserContext context)
    {
        // Empty form: JSON_OBJECT().
        if (context.Token is Operator { Character: ')' })
        {
            this.entries = [];
            this.nullClause = JsonNullClause.NullOnNull;
            return;
        }

        var list = new List<(Expression, Expression, bool)>();
        while (true)
        {
            // Key parse: temporarily redirect bare ':' to end-of-expression.
            var savedFlag = context.StopExpressionAtBareColon;
            context.StopExpressionAtBareColon = true;
            Expression key;
            try
            {
                key = Parse(context);
            }
            finally
            {
                context.StopExpressionAtBareColon = savedFlag;
            }
            if (context.Token is not Operator { Character: ':' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            var value = Parse(context);
            list.Add((key, value, JsonValueRender.ProducesJson(value)));

            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            break;
        }

        this.nullClause = JsonNullClauseParser.Parse(context, JsonNullClause.NullOnNull);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.entries = [.. list];
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var sb = new StringBuilder(16);
        _ = sb.Append('{');
        var first = true;
        foreach (var (keyExpr, valueExpr, embedRaw) in this.entries)
        {
            var valueResult = valueExpr.Run(runtime);
            if (valueResult.IsNull && this.nullClause == JsonNullClause.AbsentOnNull)
                continue;
            if (!first)
                _ = sb.Append(',');
            first = false;
            JsonValueRender.AppendKey(sb, keyExpr.Run(runtime));
            _ = sb.Append(':');
            JsonValueRender.Append(sb, valueResult, embedRaw);
        }
        _ = sb.Append('}');
        return SqlValue.FromNVarchar(SqlType.NVarcharMax, sb.ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarcharMax;

    internal override string DebugDisplay() =>
        $"JSON_OBJECT({string.Join(", ", this.entries.Select(e => $"{e.Key.DebugDisplay()}: {e.Value.DebugDisplay()}"))})";
}

/// <summary>
/// SQL <c>JSON_ARRAY([value1 [, ... valueN]] [null_clause])</c>: builds a
/// JSON array string from a positional value list. Default null clause is
/// <see cref="JsonNullClause.AbsentOnNull"/> (NULL values are omitted).
/// Result type is <see cref="SqlType.NVarcharMax"/> (<c>nvarchar(max)</c>).
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-23): empty argument
/// list yields <c>[]</c>; nested JSON-producing inputs embed raw; other
/// values format per <see cref="JsonValueRender.Append"/>.
/// </remarks>
internal sealed class JsonArray : Expression
{
    private readonly (Expression Value, bool EmbedRaw)[] items;
    private readonly JsonNullClause nullClause;

    public JsonArray(ParserContext context)
    {
        if (context.Token is Operator { Character: ')' })
        {
            this.items = [];
            this.nullClause = JsonNullClause.AbsentOnNull;
            return;
        }

        var list = new List<(Expression, bool)>();
        while (true)
        {
            var value = Parse(context);
            list.Add((value, JsonValueRender.ProducesJson(value)));
            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            break;
        }

        this.nullClause = JsonNullClauseParser.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.items = [.. list];
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var sb = new StringBuilder(16);
        _ = sb.Append('[');
        var first = true;
        foreach (var (valueExpr, embedRaw) in this.items)
        {
            var result = valueExpr.Run(runtime);
            if (result.IsNull && this.nullClause == JsonNullClause.AbsentOnNull)
                continue;
            if (!first)
                _ = sb.Append(',');
            first = false;
            JsonValueRender.Append(sb, result, embedRaw);
        }
        _ = sb.Append(']');
        return SqlValue.FromNVarchar(SqlType.NVarcharMax, sb.ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarcharMax;

    internal override string DebugDisplay() =>
        $"JSON_ARRAY({string.Join(", ", this.items.Select(i => i.Value.DebugDisplay()))})";
}
