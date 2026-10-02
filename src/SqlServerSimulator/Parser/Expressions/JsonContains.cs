using System.Text.Json;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL Server 2025's <c>JSON_CONTAINS(json, value [, path [, mode]])</c>:
/// <c>1</c> when a scalar the path selects in a <c>json</c> document equals
/// the value, <c>0</c> when the path selects values and none
/// does, NULL when the path selects nothing or the document is NULL. The
/// path takes the advanced array accessors (<see cref="JsonPath.Select"/>);
/// without one it is <c>$[*]</c> — the root array's elements, and nothing
/// from a root object. The mode is 0 for <c>=</c> and 1 for <c>LIKE</c>,
/// which only a string value reads (all probed 2026-09-27 against SQL Server
/// 2025).
/// </summary>
/// <remarks>
/// The value's SQL type decides what it can equal: a number only a JSON
/// number of the same value (<c>1.0</c> equals <c>1</c>), <c>bit</c> only
/// <c>true</c> / <c>false</c>, a string only a JSON string. A JSON string is
/// read as <c>varchar</c> in the database's collation — so a character its
/// code page lacks becomes its best fit or <c>?</c> whatever the value's
/// type — and compared under the value's collation with <c>=</c>'s trailing
/// space padding, or matched by <c>LIKE</c> with no such slack. A NULL value
/// is 0, never a match for JSON <c>null</c>. The path's <c>strict</c> keyword
/// changes nothing.
/// </remarks>
internal sealed class JsonContains : Expression
{
    /// <summary>The path an absent third argument stands for.</summary>
    private static readonly JsonPath DefaultPath = JsonPath.Parse("$[*]");

    private readonly Expression document;
    private readonly Expression value;
    private readonly Expression? path;
    private readonly Expression? mode;

    public JsonContains(ParserContext context)
    {
        var arguments = new List<Expression> { Parse(context) };
        while (context.Token is Tokens.Operator { Character: ',' })
            arguments.Add(Parse(context.MoveNextRequiredReturnSelf()));
        if (arguments.Count is < 2 or > 4)
            throw SimulatedSqlException.FunctionArgumentCountRange("json_contains", 2, 4);
        this.document = arguments[0];
        this.value = arguments[1];
        this.path = arguments.Count > 2 ? arguments[2] : null;
        this.mode = arguments.Count > 3 ? arguments[3] : null;
    }

    internal override bool ParallelSafe =>
        this.document.ParallelSafe && this.value.ParallelSafe && (this.path?.ParallelSafe ?? true) && (this.mode?.ParallelSafe ?? true);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var pathValue = this.path is null ? default : JsonText.RequirePathValue(this.path.Run(runtime), "JSON_CONTAINS");
        var documentValue = this.document.Run(runtime);
        if (documentValue.IsNull)
            return SqlValue.Null(SqlType.Int32);

        var like = false;
        if (this.mode?.Run(runtime) is { IsNull: false } modeValue)
        {
            like = modeValue.AsInt32 switch
            {
                0 => false,
                1 => true,
                _ => throw SimulatedSqlException.JsonContainsModeInvalid(),
            };
        }

        var search = this.value.Run(runtime);
        var jsonPath = this.path is null ? DefaultPath : JsonPath.Parse(pathValue.AsString);
        using var doc = JsonText.Parse(documentValue.AsString);
        var nodes = new List<JsonElement>();
        if (!jsonPath.Select(doc.RootElement, nodes, out _))
            return SqlValue.Null(SqlType.Int32);
        if (search.IsNull)
            return SqlValue.FromInt32(0);

        var database = runtime.Batch.CurrentDatabase;
        foreach (var node in nodes)
        {
            if (Matches(node, search, like, database))
                return SqlValue.FromInt32(1);
        }
        return SqlValue.FromInt32(0);
    }

    private static bool Matches(JsonElement node, SqlValue search, bool like, Database database)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Number when search.Type != SqlType.Bit && (SqlType.IsIntegerCategory(search.Type) || search.Type is DecimalSqlType):
                var text = node.GetRawText();
                var point = text.IndexOf('.', StringComparison.Ordinal);
                if (Decimal38.TryParse(text, 38, point < 0 ? 0 : text.Length - point - 1, out var number) != Decimal38ParseOutcome.Success)
                    return false;
                var wanted = search.Type is DecimalSqlType ? search.AsDecimal38 : Decimal38.FromInt64(search.CoerceTo(SqlType.BigInt).AsInt64);
                return number == wanted;
            case JsonValueKind.True or JsonValueKind.False when search.Type == SqlType.Bit:
                return search.AsBoolean == (node.ValueKind == JsonValueKind.True);
            case JsonValueKind.String when search.Type.Category == SqlTypeCategory.String:
                var candidate = SqlValue.FromNVarchar(SqlType.NVarcharMax, JsonText.StringValue(node))
                    .CoerceTo(VarcharSqlType.Get(SqlType.MaxLengthSentinel, database.Collation, Coercibility.CoercibleDefault))
                    .AsString;
                var collation = search.Type.Collation ?? database.Collation;
                return like
                    ? LikeMatcher.Compile(search.AsString, null, collation, forPatIndex: false).IsMatch(candidate, false)
                    : collation.Equals(candidate.TrimEnd(' '), search.AsString.TrimEnd(' '));
            default:
                return false;
        }
    }

    /// <summary>
    /// Real binds every argument's type while compiling (probed 2026-09-27
    /// against SQL Server 2025): the document must be <c>json</c>; the value
    /// an integer, <c>decimal</c> / <c>numeric</c>, <c>bit</c> or a non-LOB
    /// character string — <c>float</c> and <c>real</c>, money, the date/time
    /// types, binaries, <c>json</c> itself and a bare <c>NULL</c> are refused;
    /// the path a character string, a bare <c>NULL</c> reporting at state 8
    /// against argument 2 in capitals as the sibling path functions' runtime
    /// check does; and the mode <c>int</c> exactly.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var documentType = this.document.GetSqlType(batch, resolveColumnType);
        if (documentType is not JsonSqlType && !IsUntypedNullLiteral(this.document))
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(documentType, this.document), 1, "json_contains");

        if (IsUntypedNullLiteral(this.value))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", 2, "json_contains");
        var valueType = this.value.GetSqlType(batch, resolveColumnType);
        if (!(SqlType.IsIntegerCategory(valueType)
            || valueType is DecimalSqlType
            || valueType == SqlType.Bit
            || (valueType.Category == SqlTypeCategory.String && valueType is not TextSqlType and not NTextSqlType and not XmlSqlType and not SpatialSqlType)))
        {
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(valueType, this.value), 2, "json_contains");
        }

        if (this.path is not null)
        {
            if (IsUntypedNullLiteral(this.path))
                throw SimulatedSqlException.InvalidArgumentDataType("NULL", 2, "JSON_CONTAINS", state: 8);
            _ = StringScalars.RequireStringArgument(this.path, this.path.GetSqlType(batch, resolveColumnType), "json_contains", 3, acceptsLegacyLob: false);
        }

        if (this.mode is not null && !IsUntypedNullLiteral(this.mode)
            && this.mode.GetSqlType(batch, resolveColumnType) is var modeType && modeType != SqlType.Int32)
        {
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(modeType, this.mode), 4, "json_contains");
        }

        return SqlType.Int32;
    }

    internal override string DebugDisplay() =>
        $"JSON_CONTAINS({this.document.DebugDisplay()}, {this.value.DebugDisplay()}{(this.path is null ? "" : ", " + this.path.DebugDisplay())}{(this.mode is null ? "" : ", " + this.mode.DebugDisplay())})";

    internal override void Describe(NodeShape shape) => shape.Child(this.document).Child(this.value).Child(this.path).Child(this.mode);
}
