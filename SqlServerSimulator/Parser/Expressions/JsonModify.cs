using System.Text.Json;
using System.Text.Json.Nodes;
using SqlServerSimulator.Storage;
using StjValue = System.Text.Json.Nodes.JsonValue;
using StjArray = System.Text.Json.Nodes.JsonArray;
using StjObject = System.Text.Json.Nodes.JsonObject;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>JSON_MODIFY(json, path, newValue)</c>: returns an updated copy
/// of the JSON-text first argument with the value at the path replaced
/// by the third argument. Result is typed <see cref="SqlType.NVarcharMax"/>
/// (<c>nvarchar(max)</c>, matching real SQL Server) so an updated document
/// larger than the bounded 2-byte wire length prefix streams as PLP rather
/// than crashing the TDS session.
/// </summary>
/// <remarks>
/// EF Core 10 emits this from <c>OwnsOne(...).ToJson()</c> partial-update
/// paths — e.g. mutating <c>c.Address.City</c> compiles to
/// <c>UPDATE … SET [Address] = JSON_MODIFY([Address], 'strict $.City', JSON_VALUE(@p0, '$.""'))</c>.
/// The <c>strict</c> prefix is honored: a missing leaf path raises
/// Msg 13608, matching SaveChanges' implicit assumption that the owned
/// object is always fully populated.
/// </remarks>
internal sealed class JsonModify : Expression
{
    private readonly Expression jsonInput;
    private readonly Expression pathInput;
    private readonly Expression newValueInput;

    public JsonModify(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.newValueInput = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jsonInputValue = this.jsonInput.Run(runtime);
        var pathValue = this.pathInput.Run(runtime);
        var newSqlValue = this.newValueInput.Run(runtime);
        if (jsonInputValue.IsNull || pathValue.IsNull)
            return SqlValue.Null(SqlType.NVarcharMax);

        var path = JsonPath.Parse(pathValue.AsString);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(jsonInputValue.AsString);
        }
        catch (JsonException)
        {
            return path.Mode == JsonPathMode.Strict ? throw SimulatedSqlException.JsonInvalidText() : SqlValue.Null(SqlType.NVarcharMax);
        }

        if (path.Segments.Length == 0)
        {
            // Bare `$` — replaces the entire document with the new value.
            return SqlValue.FromNVarchar(SqlType.NVarcharMax, SqlValueAsJsonText(newSqlValue));
        }

        var (parent, leaf) = path.WalkForModify(root!);
        if (parent is null)
            return SqlValue.FromNVarchar(SqlType.NVarcharMax, root!.ToJsonString());

        var newNode = SqlValueToJsonNode(newSqlValue);
        if (leaf.IsIndex)
        {
            if (parent is not StjArray array)
            {
                return path.Mode == JsonPathMode.Strict
                    ? throw SimulatedSqlException.JsonStrictPathNotFound()
                    : SqlValue.FromNVarchar(SqlType.NVarcharMax, root!.ToJsonString());
            }
            if (leaf.Index < array.Count)
            {
                array[leaf.Index] = newNode;
            }
            else if (path.Mode == JsonPathMode.Strict)
            {
                throw SimulatedSqlException.JsonStrictPathNotFound();
            }
            else
            {
                array.Add((JsonNode?)newNode);
            }
        }
        else
        {
            if (parent is not StjObject obj)
            {
                return path.Mode == JsonPathMode.Strict
                    ? throw SimulatedSqlException.JsonStrictPathNotFound()
                    : SqlValue.FromNVarchar(SqlType.NVarcharMax, root!.ToJsonString());
            }
            var leafName = leaf.Property!;
            if (obj.ContainsKey(leafName))
            {
                if (newSqlValue.IsNull)
                {
                    // Lax mode: NULL on an existing key removes it.
                    _ = obj.Remove(leafName);
                }
                else
                {
                    obj[leafName] = newNode;
                }
            }
            else
            {
                if (path.Mode == JsonPathMode.Strict)
                    throw SimulatedSqlException.JsonStrictPathNotFound();
                if (!newSqlValue.IsNull)
                    obj[leafName] = newNode;
            }
        }

        return SqlValue.FromNVarchar(SqlType.NVarcharMax, root!.ToJsonString());
    }

    /// <summary>Renders a SQL value as standalone JSON text (the bare-<c>$</c> branch).</summary>
    private static string SqlValueAsJsonText(SqlValue value) =>
        value.IsNull ? "null" : SqlValueToJsonNode(value)!.ToJsonString();

    /// <summary>
    /// Converts a SQL value to a <see cref="StjValue"/> for substitution
    /// into an existing JSON document. Numbers stay JSON numbers, booleans
    /// stay JSON booleans, strings (including date/time/guid) stay JSON
    /// strings.
    /// </summary>
    private static StjValue? SqlValueToJsonNode(SqlValue value)
    {
        if (value.IsNull)
            return null;

        if (SqlType.IsStringCategory(value.Type))
            return StjValue.Create(value.AsString);

        var type = value.Type;
        return type == SqlType.Bit ? StjValue.Create(value.AsBoolean)
            : SqlType.IsIntegerCategory(type) ? StjValue.Create(IntegerAsLong(type, value))
            : type is DecimalSqlType ? StjValue.Create(value.AsDecimal)
            : SqlType.IsMoneyCategory(type) ? StjValue.Create(value.AsMoney)
            : type == SqlType.Float ? StjValue.Create(value.AsDouble)
            : type == SqlType.Real ? StjValue.Create(value.AsSingle)
            : StjValue.Create(value.AsString);
    }

    private static long IntegerAsLong(SqlType type, SqlValue value) =>
        type == SqlType.TinyInt ? value.AsByte
        : type == SqlType.SmallInt ? value.AsInt16
        : type == SqlType.Int32 ? value.AsInt32
        : value.AsInt64;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarcharMax;

    internal override string DebugDisplay() => $"JSON_MODIFY({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()}, {this.newValueInput.DebugDisplay()})";
}
