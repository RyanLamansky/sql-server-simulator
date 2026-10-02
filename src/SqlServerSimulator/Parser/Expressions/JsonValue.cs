using System.Text.Json;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>JSON_VALUE(json, path [RETURNING type])</c>: extracts a scalar value
/// (string, number, true/false, null) from a JSON text by path. Returns
/// <c>nvarchar(4000)</c>, or the <c>RETURNING</c> type over a <c>json</c>
/// document; non-scalar matches and missing-path cases yield SQL NULL under
/// the default lax mode.
/// </summary>
/// <remarks>
/// EF Core 10 emits this from <c>OwnsOne(...).ToJson()</c> read paths —
/// e.g. <c>Where(c =&gt; c.Address.City == "X")</c> compiles to
/// <c>JSON_VALUE([c].[Address], '$.City') = N'X'</c>. The lax-mode default
/// matches EF's expectations: an absent owned-type property reads as NULL
/// rather than raising.
/// </remarks>
internal sealed class JsonValue : Expression
{
    /// <summary>JSON_VALUE's <c>nvarchar(4000)</c> result cap.</summary>
    private const int MaxScalarChars = 4000;

    private readonly Expression jsonInput;
    private readonly Expression pathInput;

    /// <summary>The <c>RETURNING</c> clause's type, or null without one.</summary>
    private readonly SqlType? returningType;
    private readonly int? returningMaxLength;

    public JsonValue(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is UnquotedString { Value: var word } && Collation.Baseline.Equals(word, "RETURNING"))
            (this.returningType, this.returningMaxLength) = ParseReturning(context);
        if (context.Token is Operator { Character: ',' } && this.returningType is not null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Parses the type behind <c>RETURNING</c>: the integer types,
    /// <c>bit</c>, <c>decimal</c> / <c>numeric</c>, <c>float</c> /
    /// <c>real</c>, the four character types with a length, and <c>date</c> /
    /// <c>time</c> / <c>datetime2</c> / <c>datetimeoffset</c>. Any other type —
    /// <c>datetime</c>, money, <c>uniqueidentifier</c>, the binaries, the
    /// LOBs, <c>json</c>, an alias type — is Msg 102 state 29 near its name,
    /// and a character type without a length Msg 102 near <c>RETURNING</c>
    /// (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static (SqlType, int?) ParseReturning(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name typeName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextRequired() is Operator { Character: '.' })
        {
            var written = typeName.Value;
            while (context.Token is Operator { Character: '.' } && context.GetNextRequired() is Name part)
            {
                written += "." + part.Value;
                context.MoveNextRequired();
            }
            throw SimulatedSqlException.JsonValueReturningType(written);
        }
        var bounded = context.Token is Operator { Character: '(' };
        context.RestoreCheckpoint(checkpoint);

        Span<char> folded = stackalloc char[16];
        var lower = typeName.Value.Length <= folded.Length ? folded[..typeName.Value.AsSpan().ToLowerInvariant(folded)] : [];
        switch (lower)
        {
            case "bigint":
            case "bit":
            case "char":
            case "date":
            case "datetime2":
            case "datetimeoffset":
            case "dec":
            case "decimal":
            case "float":
            case "int":
            case "nchar":
            case "numeric":
            case "nvarchar":
            case "real":
            case "smallint":
            case "time":
            case "tinyint":
            case "varchar":
                break;
            case "vector":
                throw SimulatedSqlException.JsonValueReturningType("sys.vector");
            default:
                throw SimulatedSqlException.JsonValueReturningType(typeName.Value);
        }
        if (!bounded && lower is "char" or "nchar" or "nvarchar" or "varchar")
            throw SimulatedSqlException.SyntaxErrorNearText("RETURNING");
        return Cast.ParseTargetTypeSpec(context, typeName);
    }

    internal override bool ParallelSafe => this.jsonInput.ParallelSafe && this.pathInput.ParallelSafe;

    private static bool IsMaxForm(SqlType type) =>
        type.IsLob
        || type is VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel };

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jsonValue = this.jsonInput.Run(runtime);
        var pathValue = JsonText.RequirePathValue(this.pathInput.Run(runtime), "JSON_VALUE");
        if (jsonValue.IsNull)
            return this.NullResult(jsonValue.Type, runtime.Batch);

        var path = JsonPath.Parse(pathValue.AsString);
        if (path.IsAdvanced)
            return this.RunAdvanced(jsonValue, path, runtime.Batch);

        var scan = JsonText.Scan(jsonValue.AsString);
        var result = JsonWalkResult.Exhausted;
        if (scan.Text is not null)
        {
            using var doc = JsonText.Parse(scan.Text);
            result = path.Walk(doc.RootElement, scan, out var element);
            if (result == JsonWalkResult.Resolved)
                return this.Answer(element, path.Mode, jsonValue.Type, runtime.Batch);
        }

        JsonText.RaiseUnresolved(scan, result, path.Mode, jsonValue.Type);
        return this.NullResult(jsonValue.Type, runtime.Batch);
    }

    /// <summary>
    /// A path with SQL Server 2025's advanced accessors: one selected value
    /// answers as a plain path's would, several are NULL — Msg 13623 under
    /// <c>strict</c> over <c>json</c>, Msg 13608 state 2 over text — and none
    /// is a miss (probed 2026-09-27 against SQL Server 2025). Over text,
    /// <c>last</c> and a list are refused outright.
    /// </summary>
    private SqlValue RunAdvanced(SqlValue jsonValue, in JsonPath path, BatchContext batch)
    {
        JsonText.RejectTextAccessors(path, jsonValue.Type);
        var isJson = jsonValue.Type is JsonSqlType;
        var nodes = new List<JsonElement>();
        using var doc = JsonText.SelectAdvanced(jsonValue.AsString, path, nodes, out var found, out var partial);
        var strict = path.Mode == JsonPathMode.Strict;
        if (!found || nodes.Count == 0 || (strict && partial))
            return strict ? throw SimulatedSqlException.JsonStrictPathNotFound(isJson ? (byte)5 : (byte)2) : this.NullResult(jsonValue.Type, batch);
        if (nodes.Count > 1)
        {
            return !strict ? this.NullResult(jsonValue.Type, batch)
                : isJson ? throw SimulatedSqlException.JsonScalarNotFound()
                : throw SimulatedSqlException.JsonStrictPathNotFound(2);
        }
        return this.Answer(nodes[0], path.Mode, jsonValue.Type, batch);
    }

    private SqlValue NullResult(SqlType documentType, BatchContext batch) =>
        SqlValue.Null(this.returningType is null ? TextResult(documentType, batch) : this.ResultType(batch));

    /// <summary>
    /// <c>nvarchar(4000)</c> in the document's own collation and
    /// coercibility, the database's for a <c>json</c> document (probed
    /// 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static SqlType TextResult(SqlType documentType, BatchContext batch) =>
        JsonText.ResultIn(NVarcharSqlType.Get(MaxScalarChars, Collation.Baseline, Coercibility.CoercibleDefault), documentType, batch);

    /// <summary>The <c>RETURNING</c> type, a character type carrying the database's collation.</summary>
    private SqlType ResultType(BatchContext batch) =>
        this.returningType!.Category == SqlTypeCategory.String
            ? this.returningType.WithCollation(batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault)
            : this.returningType;

    private SqlValue Answer(JsonElement element, JsonPathMode mode, SqlType documentType, BatchContext batch)
    {
        if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            // There is no scalar to hand back, which lax mode answers as NULL
            // and strict raises Msg 13623 for.
            return mode == JsonPathMode.Strict ? throw SimulatedSqlException.JsonScalarNotFound() : this.NullResult(documentType, batch);
        }
        if (this.returningType is not null)
            return this.Convert(element, mode == JsonPathMode.Strict, batch);

        var type = TextResult(documentType, batch);
        return element.ValueKind switch
        {
            // JSON_VALUE returns nvarchar(4000). A longer scalar string splits
            // by the input's type (probe-confirmed against SQL Server 2025):
            // over a MAX document it is NULL in the default lax mode (4000 →
            // value, 4001 → NULL) and Msg 13625 under strict, over a bounded
            // one it is cut to its first 4000 characters in either mode
            // (2026-09-23, 2026-10-02). Either way the result stays within the
            // bounded wire prefix.
            JsonValueKind.String => JsonText.StringValue(element) switch
            {
                { Length: <= MaxScalarChars } s => SqlValue.FromString(type, s),
                { } s when !IsMaxForm(documentType) => SqlValue.FromString(type, s[..MaxScalarChars]),
                _ when mode == JsonPathMode.Strict => throw SimulatedSqlException.JsonValueTruncated(),
                _ => SqlValue.Null(type),
            },
            JsonValueKind.Number => SqlValue.FromString(type, element.GetRawText()),
            JsonValueKind.True => SqlValue.FromString(type, "true"),
            JsonValueKind.False => SqlValue.FromString(type, "false"),
            _ => SqlValue.Null(type),
        };
    }

    /// <summary>
    /// Converts a scalar to the <c>RETURNING</c> type the way real does
    /// (probed 2026-09-27 against SQL Server 2025): a string is read as
    /// <c>varchar</c> in the database's collation, a whole number as
    /// <c>int</c> or <c>bigint</c> and any other as <c>decimal</c>, and
    /// <c>true</c> / <c>false</c> as <c>bit</c> — or as their own text for a
    /// character target. A failed conversion, an overflow and a string too
    /// long for a bounded character target are NULL in lax mode and the
    /// conversion's own error (Msg 8152 state 34 for the length) under
    /// <c>strict</c>; a conversion real never allows is Msg 529 in either.
    /// </summary>
    private SqlValue Convert(JsonElement element, bool strict, BatchContext batch)
    {
        var target = this.ResultType(batch);
        var collation = batch.CurrentDatabase.Collation;
        var stringTarget = target.Category == SqlTypeCategory.String;
        SqlValue source;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return SqlValue.Null(target);
            case JsonValueKind.String:
                source = SqlValue.FromNVarchar(SqlType.NVarcharMax, JsonText.StringValue(element))
                    .CoerceTo(VarcharSqlType.Get(SqlType.MaxLengthSentinel, collation, Coercibility.CoercibleDefault));
                break;
            case JsonValueKind.True or JsonValueKind.False when stringTarget:
                source = SqlValue.FromVarchar(VarcharSqlType.Get(SqlType.MaxLengthSentinel, collation, Coercibility.CoercibleDefault), element.ValueKind == JsonValueKind.True ? "true" : "false");
                break;
            case JsonValueKind.True or JsonValueKind.False:
                source = SqlValue.FromBoolean(element.ValueKind == JsonValueKind.True);
                break;
            default:
                source = NumberValue(element.GetRawText());
                break;
        }

        if (stringTarget && source.Type.Category == SqlTypeCategory.String
            && this.returningMaxLength is > 0 and var limit && limit != SqlType.MaxLengthSentinel && source.AsString.Length > limit)
        {
            return strict ? throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy(34) : SqlValue.Null(target);
        }

        try
        {
            var coerced = Cast.ApplyCoercion(source, target, this.returningMaxLength, stringTarget ? collation : null);
            return stringTarget ? Cast.RecollateStringResult(coerced, target, target, collation) : coerced;
        }
        catch (SimulatedSqlException ex) when (!strict && Cast.IsConversionFailure(ex.Number))
        {
            return SqlValue.Null(target);
        }
    }

    /// <summary>
    /// A JSON number typed as a literal of its digits would be: <c>int</c>,
    /// then <c>bigint</c>, then <c>decimal</c> at its own precision and scale.
    /// </summary>
    private static SqlValue NumberValue(string text)
    {
        if (!text.Contains('.', StringComparison.Ordinal) && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var whole))
            return whole is >= int.MinValue and <= int.MaxValue ? SqlValue.FromInt32((int)whole) : SqlValue.FromInt64(whole);
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var scale = point < 0 ? 0 : text.Length - point - 1;
        var digits = text.Count(char.IsAsciiDigit);
        var precision = Math.Clamp(digits, Math.Max(scale, 1), 38);
        _ = Decimal38.TryParse(text, precision, scale, out var number);
        return SqlValue.FromDecimal(SqlType.GetDecimal(precision, scale), number);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        JsonText.RequireDocumentAndPath(this.jsonInput, this.pathInput, batch, resolveColumnType, "json_value");
        if (this.returningType is null)
            return TextResult(this.jsonInput.GetSqlType(batch, resolveColumnType), batch);

        // RETURNING is json-only: over a text document real reports the
        // clause itself as a syntax error.
        return this.jsonInput.GetSqlType(batch, resolveColumnType) is JsonSqlType || IsUntypedNullLiteral(this.jsonInput)
            ? this.ResultType(batch)
            : throw SimulatedSqlException.SyntaxErrorNearText("RETURNING");
    }

    internal override string DebugDisplay() => $"JSON_VALUE({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()}{(this.returningType is null ? "" : $" RETURNING {this.returningType}")})";

    internal override void Describe(NodeShape shape) => shape.Local(this.returningType).Local(this.returningMaxLength).Child(this.jsonInput).Child(this.pathInput);
}
