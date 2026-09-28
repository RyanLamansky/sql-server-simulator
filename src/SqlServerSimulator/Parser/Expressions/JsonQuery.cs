using System.Text;
using System.Text.Json;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>JSON_QUERY(json, path)</c>: extracts an object or array subtree
/// from a JSON text by path. Returns <c>nvarchar(MAX)</c>; scalar matches
/// and missing-path cases yield SQL NULL under the default lax mode —
/// complement of <see cref="JsonValue"/> (which returns NULL for non-scalar
/// matches and the scalar text otherwise).
/// </summary>
/// <remarks>
/// The path argument is optional: <c>JSON_QUERY(json)</c> is shorthand for
/// <c>JSON_QUERY(json, '$')</c> and hands back the whole document (whitespace
/// shape preserved, outer padding dropped) when it parses as a JSON object or
/// array. DACFx-emitted computed columns (WWI's
/// <c>Application.People.OtherLanguages</c>, <c>Warehouse.StockItems.Tags</c>)
/// supply an explicit path. A third argument raises Msg 189.
/// </remarks>
internal sealed class JsonQuery : Expression
{
    private readonly Expression jsonInput;

    /// <summary>
    /// The path expression, or null for the 1-argument form — which behaves as
    /// the lax <c>'$'</c> path rather than allocating a literal for it.
    /// </summary>
    private readonly Expression? pathInput;

    /// <summary>
    /// Whether SQL Server 2025's <c>WITH ARRAY WRAPPER</c> closes the
    /// argument list, which gathers everything the path selects into one array.
    /// </summary>
    private readonly bool arrayWrapper;

    /// <summary>The result type the statement bound, which the rows carry.</summary>
    private SqlType? resultType;

    public JsonQuery(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is Operator { Character: ',' })
        {
            this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Operator { Character: ',' })
                throw SimulatedSqlException.FunctionArgumentCountRange("json_query", 1, 2);
        }
        this.arrayWrapper = ParseArrayWrapper(context);
    }

    /// <summary>
    /// Reads <c>WITH ARRAY WRAPPER</c> (any case). Real takes no other
    /// wrapper form — <c>CONDITIONAL</c>, <c>UNCONDITIONAL</c> and
    /// <c>WITHOUT</c> are Msg 102 at the word, and a bare <c>WITH WRAPPER</c>
    /// at the token after it (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static bool ParseArrayWrapper(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return false;
        context.MoveNextRequired();
        if (IsWord(context, "WRAPPER"))
            throw SimulatedSqlException.SyntaxErrorNear(context.MoveNextRequiredReturnSelf());
        if (!IsWord(context, "ARRAY"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (!IsWord(context, "WRAPPER"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        return true;

        static bool IsWord(ParserContext context, string word) =>
            context.Token is StringToken { Value: var written } && Collation.Baseline.Equals(written, word);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jsonValue = this.jsonInput.Run(runtime);
        var resultType = this.resultType ?? ResultType(jsonValue.Type);
        if (this.pathInput is null)
        {
            return jsonValue.IsNull ? SqlValue.Null(resultType)
                : this.arrayWrapper ? Wrap(jsonValue, JsonPath.Root, resultType)
                : Extract(jsonValue, JsonPath.Root, resultType);
        }

        var pathValue = JsonText.RequirePathValue(this.pathInput.Run(runtime), "JSON_QUERY");
        if (jsonValue.IsNull)
            return SqlValue.Null(resultType);
        var path = JsonPath.Parse(pathValue.AsString);
        return this.arrayWrapper ? Wrap(jsonValue, path, resultType)
            : path.IsAdvanced ? ExtractAdvanced(jsonValue, path, resultType)
            : Extract(jsonValue, path, resultType);
    }

    /// <summary>
    /// <c>WITH ARRAY WRAPPER</c>: every value the path selects, scalars
    /// included, as the elements of one array — <c>[]</c> for a wildcard over
    /// an empty array, NULL for a path that finds nothing, and under
    /// <c>strict</c> Msg 13608 for a path that misses anywhere, state 5 over
    /// <c>json</c> and 2 over text. Over text each container keeps its own
    /// spacing and each string is re-escaped the way the JSON builders write
    /// one (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static SqlValue Wrap(SqlValue jsonValue, in JsonPath path, SqlType resultType)
    {
        JsonText.RejectTextAccessors(path, jsonValue.Type);
        var isJson = jsonValue.Type is JsonSqlType;
        var strict = path.Mode == JsonPathMode.Strict;
        var nodes = new List<JsonElement>();
        using var doc = JsonText.SelectAdvanced(jsonValue.AsString, path, nodes, out var found, out var partial);
        if (!found || (strict && partial))
            return strict ? throw SimulatedSqlException.JsonStrictPathNotFound(isJson ? (byte)5 : (byte)2) : SqlValue.Null(resultType);

        var sb = new StringBuilder("[");
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
                _ = sb.Append(',');
            if (!isJson && nodes[i].ValueKind == JsonValueKind.String)
                JsonValueRender.AppendJsonString(sb, nodes[i].GetString()!, escapeSolidus: true);
            else
                _ = sb.Append(nodes[i].GetRawText());
        }
        var text = sb.Append(']').ToString();
        return isJson ? SqlValue.FromJson(text) : SqlValue.FromString(resultType, text);
    }

    /// <summary>
    /// A path with SQL Server 2025's advanced accessors and no wrapper: one
    /// selected value answers as a plain path's would; several are NULL, or
    /// Msg 13624 under <c>strict</c> (probed 2026-09-27 against SQL Server
    /// 2025). Over text, <c>last</c> and a list are refused outright.
    /// </summary>
    private static SqlValue ExtractAdvanced(SqlValue jsonValue, in JsonPath path, SqlType resultType)
    {
        JsonText.RejectTextAccessors(path, jsonValue.Type);
        var isJson = jsonValue.Type is JsonSqlType;
        var strict = path.Mode == JsonPathMode.Strict;
        var nodes = new List<JsonElement>();
        using var doc = JsonText.SelectAdvanced(jsonValue.AsString, path, nodes, out var found, out var partial);
        if (!found || nodes.Count == 0 || (strict && partial))
            return strict ? throw SimulatedSqlException.JsonStrictPathNotFound(isJson ? (byte)5 : (byte)2) : SqlValue.Null(resultType);
        if (nodes.Count > 1)
            return strict ? throw SimulatedSqlException.JsonObjectOrArrayNotFound(2) : SqlValue.Null(resultType);
        var subtree = JsonSubtree.Extract(nodes[0], path.Mode, strictScalarState: 2);
        return subtree is null ? SqlValue.Null(resultType)
            : isJson ? SqlValue.FromJson(subtree)
            : SqlValue.FromString(resultType, subtree);
    }

    /// <summary>
    /// Walks <paramref name="path"/> over the parsed document and renders the
    /// matched object / array subtree.
    /// </summary>
    private static SqlValue Extract(SqlValue jsonValue, JsonPath path, SqlType resultType)
    {
        var scan = JsonText.Scan(jsonValue.AsString);
        var result = JsonWalkResult.Exhausted;
        if (scan.Text is not null)
        {
            using var doc = JsonText.Parse(scan.Text);
            result = path.Walk(doc.RootElement, scan, out var match);
            if (result == JsonWalkResult.Resolved)
            {
                var subtree = JsonSubtree.Extract(match, path.Mode, strictScalarState: 2);
                return subtree is null ? SqlValue.Null(resultType)
                    : jsonValue.Type is JsonSqlType ? SqlValue.FromJson(subtree)
                    : SqlValue.FromString(resultType, subtree);
            }
        }

        JsonText.RaiseUnresolved(scan, result, path.Mode, jsonValue.Type);
        return SqlValue.Null(resultType);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        JsonText.RequireDocumentAndPath(this.jsonInput, this.pathInput, batch, resolveColumnType, "json_query");
        return this.resultType = ResultType(this.jsonInput.GetSqlType(batch, resolveColumnType));
    }

    /// <summary>
    /// <c>json</c> over a <c>json</c> document — the subtree of a canonical
    /// document is itself canonical — <c>nvarchar(max)</c> over a MAX string,
    /// and <c>nvarchar(4000)</c> over any other text, a literal included
    /// (probed 2026-09-26 and 2026-09-28 against SQL Server 2025).
    /// </summary>
    private static SqlType ResultType(SqlType documentType) => documentType switch
    {
        JsonSqlType => SqlType.Json,
        VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel } => SqlType.NVarcharMax,
        _ => SqlType.NVarchar,
    };

    internal override string DebugDisplay() => this.pathInput is null
        ? $"JSON_QUERY({this.jsonInput.DebugDisplay()}{(this.arrayWrapper ? " WITH ARRAY WRAPPER" : "")})"
        : $"JSON_QUERY({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()}{(this.arrayWrapper ? " WITH ARRAY WRAPPER" : "")})";

    internal override void Describe(NodeShape shape) => shape.Local(this.arrayWrapper).Child(this.jsonInput).Child(this.pathInput);
}
