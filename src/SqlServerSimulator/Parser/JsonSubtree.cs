using System.Text.Json;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The JSON_QUERY-style subtree-extraction rule shared by <c>JSON_QUERY</c>
/// and the <c>OPENJSON … WITH (col … AS JSON)</c> column modifier: an
/// object/array match yields its verbatim source text (whitespace and
/// key order preserved, via <see cref="JsonElement.GetRawText"/>), a JSON
/// <c>null</c> yields SQL NULL, and any other (non-null) scalar yields SQL
/// NULL under lax mode or Msg 13624 under strict.
/// </summary>
internal static class JsonSubtree
{
    /// <summary>
    /// Classifies an already-resolved JSON element. Returns the verbatim
    /// subtree text for an object/array; <c>null</c> for a JSON-null match
    /// or — under lax mode — a non-null scalar. Raises Msg 13624 for a
    /// non-null scalar under strict mode.
    /// </summary>
    public static string? Extract(JsonElement element, JsonPathMode mode) => element.ValueKind switch
    {
        JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
        JsonValueKind.Null => null,
        _ => mode == JsonPathMode.Strict ? throw SimulatedSqlException.JsonObjectOrArrayNotFound() : null,
    };
}
