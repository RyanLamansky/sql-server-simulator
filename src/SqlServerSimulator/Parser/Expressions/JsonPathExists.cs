using System.Text.Json;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>JSON_PATH_EXISTS(json, path)</c>: returns <c>1</c> when the path
/// resolves to an existing value in the JSON document, <c>0</c> otherwise.
/// NULL on either input returns NULL. Invalid JSON returns 0 under
/// lax mode (the path-prefix default), and raises Msg 13609 under strict
/// mode (matching <see cref="JsonValue"/>'s parity). Result type is
/// <see cref="SqlType.Bit"/>.
/// </summary>
internal sealed class JsonPathExists : Expression
{
    private readonly Expression jsonInput;
    private readonly Expression pathInput;

    public JsonPathExists(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jv = this.jsonInput.Run(runtime);
        var pv = this.pathInput.Run(runtime);
        if (jv.IsNull || pv.IsNull)
            return SqlValue.Null(SqlType.Bit);
        var path = JsonPath.Parse(pv.AsString);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jv.AsString);
        }
        catch (JsonException)
        {
            return path.Mode == JsonPathMode.Strict
                ? throw SimulatedSqlException.JsonInvalidText()
                : SqlValue.FromBoolean(false);
        }
        using (doc)
        {
            return SqlValue.FromBoolean(path.Walk(doc.RootElement) is not null);
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Bit;

    internal override string DebugDisplay() => $"JSON_PATH_EXISTS({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()})";
}
