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
/// The path argument is optional in real SQL Server (default <c>'$'</c> —
/// returns the whole document). DACFx-emitted computed columns (WWI's
/// <c>Application.People.OtherLanguages</c>, <c>Warehouse.StockItems.Tags</c>)
/// supply an explicit path so this implementation requires it; the bare
/// 1-arg form raises Msg 102 at parse via SyntaxErrorNear.
/// </remarks>
internal sealed class JsonQuery : Expression
{
    private readonly Expression jsonInput;
    private readonly Expression pathInput;

    public JsonQuery(ParserContext context)
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

            var subtree = JsonSubtree.Extract(match.Value, path.Mode);
            return subtree is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(subtree);
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"JSON_QUERY({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()})";
}
