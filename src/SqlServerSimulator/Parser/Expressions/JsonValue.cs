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
    /// <summary>JSON_VALUE's <c>nvarchar(4000)</c> result cap.</summary>
    private const int MaxScalarChars = 4000;

    private readonly Expression jsonInput;
    private readonly Expression pathInput;

    public JsonValue(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
    }

    internal override bool ParallelSafe => this.jsonInput.ParallelSafe && this.pathInput.ParallelSafe;

    private static bool IsMaxForm(SqlType type) =>
        type.IsLob
        || type is VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel };

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jsonValue = this.jsonInput.Run(runtime);
        var pathValue = this.pathInput.Run(runtime);
        if (jsonValue.IsNull || pathValue.IsNull)
            return SqlValue.Null(SqlType.NVarchar);

        var path = JsonPath.Parse(pathValue.AsString);
        var scan = JsonText.Scan(jsonValue.AsString);
        var result = JsonWalkResult.Exhausted;
        if (scan.Text is not null)
        {
            using var doc = JsonText.Parse(scan.Text);
            result = path.Walk(doc.RootElement, scan, out var element);
            if (result == JsonWalkResult.Resolved)
            {
                return element.ValueKind switch
                {
                    // JSON_VALUE returns nvarchar(4000). A longer scalar string
                    // splits by the input's type (probe-confirmed against SQL
                    // Server 2025): over a MAX document it is NULL in the default
                    // lax mode (4000 → value, 4001 → NULL), over a bounded one it
                    // is cut to its first 4000 characters (2026-09-23). Either
                    // way the result stays within the bounded wire prefix.
                    JsonValueKind.String => element.GetString() switch
                    {
                        { Length: <= MaxScalarChars } s => SqlValue.FromNVarchar(s),
                        { } s when !IsMaxForm(jsonValue.Type) => SqlValue.FromNVarchar(s[..MaxScalarChars]),
                        _ => SqlValue.Null(SqlType.NVarchar),
                    },
                    JsonValueKind.Number => SqlValue.FromNVarchar(element.GetRawText()),
                    JsonValueKind.True => SqlValue.FromNVarchar("true"),
                    JsonValueKind.False => SqlValue.FromNVarchar("false"),
                    JsonValueKind.Null => SqlValue.Null(SqlType.NVarchar),
                    // Object / Array — there is no scalar to hand back, which
                    // lax mode answers as NULL and strict raises Msg 13623 for.
                    _ => path.Mode == JsonPathMode.Strict ? throw SimulatedSqlException.JsonScalarNotFound() : SqlValue.Null(SqlType.NVarchar),
                };
            }
        }

        JsonText.RaiseUnresolved(scan, result, path.Mode);
        return SqlValue.Null(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"JSON_VALUE({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.jsonInput).Child(this.pathInput);
}
