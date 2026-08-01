using System.Text.Json;
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

    public JsonQuery(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            return;
        this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionArgumentCountRange("json_query", 1, 2);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jsonValue = this.jsonInput.Run(runtime);
        if (jsonValue.IsNull)
            return SqlValue.Null(SqlType.NVarcharMax);
        if (this.pathInput is null)
            return Extract(jsonValue, JsonPath.Root);

        var pathValue = this.pathInput.Run(runtime);
        return pathValue.IsNull
            ? SqlValue.Null(SqlType.NVarcharMax)
            : Extract(jsonValue, JsonPath.Parse(pathValue.AsString));
    }

    /// <summary>
    /// Walks <paramref name="path"/> over the parsed document and renders the
    /// matched object / array subtree.
    /// </summary>
    private static SqlValue Extract(SqlValue jsonValue, JsonPath path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonValue.AsString);
        }
        catch (JsonException)
        {
            return path.Mode == JsonPathMode.Strict ? throw SimulatedSqlException.JsonInvalidText() : SqlValue.Null(SqlType.NVarcharMax);
        }

        using (doc)
        {
            var match = path.Walk(doc.RootElement);
            if (match is null)
                return SqlValue.Null(SqlType.NVarcharMax);

            var subtree = JsonSubtree.Extract(match.Value, path.Mode);
            return subtree is null ? SqlValue.Null(SqlType.NVarcharMax) : SqlValue.FromNVarchar(SqlType.NVarcharMax, subtree);
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarcharMax;

    internal override string DebugDisplay() => this.pathInput is null
        ? $"JSON_QUERY({this.jsonInput.DebugDisplay()})"
        : $"JSON_QUERY({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()})";
}
