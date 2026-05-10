using System.Text.Json;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>JSON_VALUE(json, path)</c>: extracts a scalar value (string,
/// number, true/false, null) from a JSON text by path. Returns
/// <c>nvarchar(4000)</c>; non-scalar matches and missing-path cases yield
/// SQL NULL under the default lax mode.
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
    private readonly Expression jsonInput;
    private readonly Expression pathInput;

    public JsonValue(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jsonValue = this.jsonInput.Run(runtime);
        var pathValue = this.pathInput.Run(runtime);
        if (jsonValue.IsNull || pathValue.IsNull)
            return SqlValue.Null(SqlType.NVarchar);

        var path = JsonPath.Parse(pathValue.AsString);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonValue.AsString);
        }
        catch (JsonException)
        {
            return path.Mode == JsonPathMode.Strict ? throw SimulatedSqlException.JsonInvalidText() : SqlValue.Null(SqlType.NVarchar);
        }

        using (doc)
        {
            var match = path.Walk(doc.RootElement);
            if (match is null)
                return SqlValue.Null(SqlType.NVarchar);

            var element = match.Value;
            return element.ValueKind switch
            {
                JsonValueKind.String => SqlValue.FromNVarchar(element.GetString()!),
                JsonValueKind.Number => SqlValue.FromNVarchar(element.GetRawText()),
                JsonValueKind.True => SqlValue.FromNVarchar("true"),
                JsonValueKind.False => SqlValue.FromNVarchar("false"),
                JsonValueKind.Null => SqlValue.Null(SqlType.NVarchar),
                // Object / Array — JSON_VALUE returns NULL on non-scalar
                // matches in lax mode (the documented behavior); strict
                // raises Msg 13623, which EF Core never depends on.
                _ => SqlValue.Null(SqlType.NVarchar),
            };
        }
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"JSON_VALUE({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()})";
}
