using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlServerSimulator.Parser;

/// <summary>
/// SQL Server JSON path expression — the second argument shape for
/// <c>JSON_VALUE</c> / <c>JSON_QUERY</c> / <c>JSON_MODIFY</c> and the
/// per-column path inside <c>OPENJSON … WITH (col TYPE 'path')</c>.
/// </summary>
/// <remarks>
/// <para>
/// Grammar: <c>['lax' | 'strict']? '$' (segment)*</c> where <c>segment</c> is
/// either <c>.&lt;ident&gt;</c> / <c>."&lt;quoted&gt;"</c> (property access; the
/// quoted form lets EF Core embed values as <c>{"":"...."}</c> + path
/// <c>$.""</c>) or <c>[&lt;n&gt;]</c> (array index access).
/// </para>
/// <para>
/// Quoted-property escape: a doubled <c>""</c> inside the quoted form is
/// one literal <c>"</c>, matching SQL Server. Other JSON Pointer-style
/// escapes aren't modeled — EF Core 10 doesn't depend on them.
/// </para>
/// </remarks>
internal readonly struct JsonPath
{
    public readonly JsonPathMode Mode;
    public readonly Segment[] Segments;

    private JsonPath(JsonPathMode mode, Segment[] segments)
    {
        this.Mode = mode;
        this.Segments = segments;
    }

    /// <summary>
    /// Parses the path text. Throws <see cref="SimulatedSqlException"/>
    /// (Msg 13607) on a syntactically invalid path. The empty segment list
    /// (just <c>$</c>) is valid — it self-references the current element.
    /// </summary>
    public static JsonPath Parse(string text)
    {
        var i = 0;
        var mode = JsonPathMode.Lax;
        SkipWhitespace(text, ref i);
        if (i + 4 <= text.Length && text.AsSpan(i, 4).Equals("lax ", StringComparison.OrdinalIgnoreCase))
        {
            i += 4;
            SkipWhitespace(text, ref i);
        }
        else if (i + 7 <= text.Length && text.AsSpan(i, 7).Equals("strict ", StringComparison.OrdinalIgnoreCase))
        {
            mode = JsonPathMode.Strict;
            i += 7;
            SkipWhitespace(text, ref i);
        }

        if (i >= text.Length || text[i] != '$')
            throw SimulatedSqlException.JsonInvalidPath(text);
        i++;

        var segments = new List<Segment>();
        while (i < text.Length)
        {
            if (text[i] == '.')
            {
                i++;
                if (i < text.Length && text[i] == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                _ = sb.Append('"');
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        _ = sb.Append(text[i]);
                        i++;
                    }
                    segments.Add(Segment.ForProperty(sb.ToString()));
                }
                else
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;
                    if (i == start)
                        throw SimulatedSqlException.JsonInvalidPath(text);
                    segments.Add(Segment.ForProperty(text[start..i]));
                }
            }
            else if (text[i] == '[')
            {
                i++;
                var start = i;
                while (i < text.Length && char.IsDigit(text[i]))
                    i++;
                if (i == start || i >= text.Length || text[i] != ']')
                    throw SimulatedSqlException.JsonInvalidPath(text);
                var index = int.Parse(text.AsSpan(start, i - start), CultureInfo.InvariantCulture);
                i++;
                segments.Add(Segment.ForIndex(index));
            }
            else
            {
                throw SimulatedSqlException.JsonInvalidPath(text);
            }
        }

        return new JsonPath(mode, [.. segments]);
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && text[i] == ' ')
            i++;
    }

    /// <summary>
    /// Walks <paramref name="root"/> through <see cref="Segments"/>. Returns
    /// the matched element or null when a segment misses (lax mode);
    /// raises Msg 13608 in strict mode. The "missing" cases are (a) property
    /// name not present in an object, (b) array index out of bounds,
    /// (c) traversing into a non-object/non-array. <paramref name="strictNotFoundState"/>
    /// carries the caller's context-specific Msg 13608 State byte (OPENJSON
    /// columns report 6; the default 1 matches JSON_QUERY).
    /// </summary>
    public JsonElement? Walk(JsonElement root, byte strictNotFoundState = 1)
    {
        var current = root;
        foreach (var segment in this.Segments)
        {
            var next = TryStep(current, segment);
            if (next is null)
                return this.Mode == JsonPathMode.Strict ? throw SimulatedSqlException.JsonStrictPathNotFound(strictNotFoundState) : null;
            current = next.Value;
        }
        return current;
    }

    private static JsonElement? TryStep(JsonElement current, Segment segment) => segment.IsIndex
        ? (current.ValueKind == JsonValueKind.Array && segment.Index < current.GetArrayLength()
            ? current[segment.Index]
            : null)
        : (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment.Property!, out var found)
            ? found
            : null);

    /// <summary>
    /// Locates the parent node and leaf segment for <c>JSON_MODIFY</c>. The
    /// parent is the container that holds the slot named by the last
    /// segment; the leaf carries the slot's name (property) or index
    /// (array). Lax mode: returns <c>(null, default)</c> when an
    /// intermediate is missing — JSON_MODIFY treats this as a no-op and
    /// returns the input document unchanged. Strict mode: raises
    /// Msg 13608 instead.
    /// </summary>
    public (JsonNode? Parent, Segment Leaf) WalkForModify(JsonNode root)
    {
        if (this.Segments.Length == 0)
            return (null, default);

        var current = root;
        for (var i = 0; i < this.Segments.Length - 1; i++)
        {
            var next = TryStepNode(current, this.Segments[i]);
            if (next is null)
                return this.Mode == JsonPathMode.Strict ? throw SimulatedSqlException.JsonStrictPathNotFound() : (null, default);
            current = next;
        }
        return (current, this.Segments[^1]);
    }

    private static JsonNode? TryStepNode(JsonNode current, Segment segment) => segment.IsIndex
        ? (current is JsonArray array && segment.Index < array.Count ? array[segment.Index] : null)
        : (current is JsonObject obj && obj.TryGetPropertyValue(segment.Property!, out var found) ? found : null);

    /// <summary>One segment of a <see cref="JsonPath"/>: either a property
    /// access (named) or an array index access. <see cref="IsIndex"/>
    /// discriminates.</summary>
    public readonly struct Segment
    {
        public readonly bool IsIndex;
        public readonly int Index;
        public readonly string? Property;

        private Segment(bool isIndex, int index, string? property)
        {
            this.IsIndex = isIndex;
            this.Index = index;
            this.Property = property;
        }

        public static Segment ForProperty(string name) => new(false, 0, name);
        public static Segment ForIndex(int index) => new(true, index, null);
    }
}

/// <summary>
/// Lax (the default) returns NULL on missing paths / type mismatches;
/// strict raises Msg 13608. EF Core 10 emits <c>strict</c> only inside
/// <c>JSON_MODIFY</c>'s path argument; <c>JSON_VALUE</c> usage is always
/// lax (the prefix is omitted, so <see cref="Lax"/> is the default).
/// </summary>
internal enum JsonPathMode
{
    Lax,
    Strict,
}
