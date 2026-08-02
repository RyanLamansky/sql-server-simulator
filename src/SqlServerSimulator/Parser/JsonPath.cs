using System.Globalization;
using System.Text;
using System.Text.Json;

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

    /// <summary>
    /// Whether the path carried the <c>append</c> prefix, which turns
    /// <c>JSON_MODIFY</c>'s write into an append onto the array the path
    /// names. Only <c>JSON_MODIFY</c> takes the prefix; everywhere else it is
    /// Msg 13607, so <see cref="Parse"/> reads it only when asked to.
    /// </summary>
    public readonly bool Append;

    private JsonPath(JsonPathMode mode, Segment[] segments, bool append)
    {
        this.Mode = mode;
        this.Segments = segments;
        this.Append = append;
    }

    /// <summary>
    /// The bare lax <c>$</c> path — the whole document. Backs the path-less
    /// <c>JSON_QUERY(json)</c> form without re-parsing a literal per row.
    /// </summary>
    public static readonly JsonPath Root = Parse("$");

    /// <summary>
    /// Parses the path text. Throws <see cref="SimulatedSqlException"/>
    /// (Msg 13607) on a syntactically invalid path. The empty segment list
    /// (just <c>$</c>) is valid — it self-references the current element.
    /// <paramref name="acceptAppend"/> admits the <c>append</c> prefix, which
    /// only <c>JSON_MODIFY</c> takes and which precedes the
    /// <c>lax</c> / <c>strict</c> keyword rather than following it.
    /// </summary>
    public static JsonPath Parse(string text, bool acceptAppend = false)
    {
        var i = 0;
        var mode = JsonPathMode.Lax;
        var append = false;
        SkipWhitespace(text, ref i);
        if (acceptAppend && i + 7 <= text.Length && text.AsSpan(i, 7).Equals("append ", StringComparison.OrdinalIgnoreCase))
        {
            append = true;
            i += 7;
            SkipWhitespace(text, ref i);
        }

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

        return new JsonPath(mode, [.. segments], append);
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

    /// <summary>
    /// Walks <paramref name="root"/> without raising, reporting how far the
    /// reader had to get: the strict-mode Msg 13608 is the caller's to raise,
    /// because a malformed document's Msg 13609 comes first, and settling the
    /// path often costs less reading than the whole document.
    /// <paramref name="scan"/> supplies the shape of what
    /// <see cref="JsonText.Scan"/> handed back.
    /// </summary>
    public JsonWalkResult Walk(JsonElement root, in JsonScan scan, out JsonElement match)
    {
        var current = root;
        var lastChildChain = true;
        for (var i = 0; i < this.Segments.Length; i++)
        {
            var segment = this.Segments[i];

            // Asking an object for an element (or an array for a property)
            // settles the path once the reader has the container's first
            // member under way. A container with no member to start on
            // doesn't settle it any sooner than searching it would — unless
            // the scan stopped partway through one the repair dropped, which
            // is a member the reader did get under way.
            if (current.ValueKind == (segment.IsIndex ? JsonValueKind.Object : JsonValueKind.Array)
                && (!IsEmpty(current) || !scan.CleanCut))
            {
                match = default;
                return JsonWalkResult.Abandoned;
            }

            var next = TryStep(current, segment);
            if (next is null)
            {
                match = default;

                // Settling this took reading `current` to its end. The reader
                // then unwinds through the containers above, and reaches the
                // document's own problem only from the root itself, or from a
                // node that was the last member at every level with nothing
                // dropped between it and where the scan stopped.
                return i == 0 || (lastChildChain && scan.CleanCut) ? JsonWalkResult.Exhausted : JsonWalkResult.Abandoned;
            }

            lastChildChain = lastChildChain && SelectsLastChild(current, segment);
            current = next.Value;
        }

        match = current;
        return lastChildChain && this.Segments.Length < scan.OpenDepth ? JsonWalkResult.Truncated : JsonWalkResult.Resolved;
    }

    private static bool IsEmpty(JsonElement current)
    {
        if (current.ValueKind == JsonValueKind.Array)
            return current.GetArrayLength() == 0;
        foreach (var _ in current.EnumerateObject())
            return false;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="segment"/> selects the last member of
    /// <paramref name="current"/> — the direction a truncated document's
    /// unclosed containers always run in. A repeated property name counts
    /// only where the reader would have stopped, at its first occurrence.
    /// </summary>
    private static bool SelectsLastChild(JsonElement current, Segment segment)
    {
        if (segment.IsIndex)
            return current.ValueKind == JsonValueKind.Array && segment.Index == current.GetArrayLength() - 1;
        if (current.ValueKind != JsonValueKind.Object)
            return false;
        var index = 0;
        var firstMatch = -1;
        foreach (var property in current.EnumerateObject())
        {
            if (firstMatch < 0 && string.Equals(property.Name, segment.Property, StringComparison.Ordinal))
                firstMatch = index;
            index++;
        }
        return firstMatch >= 0 && firstMatch == index - 1;
    }

    private static JsonElement? TryStep(JsonElement current, Segment segment) => segment.IsIndex
        ? (current.ValueKind == JsonValueKind.Array && segment.Index < current.GetArrayLength()
            ? current[segment.Index]
            : null)
        : (current.ValueKind == JsonValueKind.Object ? FirstProperty(current, segment.Property!) : null);

    /// <summary>
    /// The first member of <paramref name="current"/> named
    /// <paramref name="name"/>. SQL Server's reader stops at the first match,
    /// so <c>JSON_VALUE('{"a":1,"a":2}', '$.a')</c> is <c>1</c>;
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
    /// hands back the last instead.
    /// </summary>
    private static JsonElement? FirstProperty(JsonElement current, string name)
    {
        foreach (var property in current.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
                return property.Value;
        }
        return null;
    }

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
/// How a <see cref="JsonPath.Walk(System.Text.Json.JsonElement, in JsonScan, out System.Text.Json.JsonElement)"/>
/// ended — which decides whether a malformed document's Msg 13609 is still
/// ahead of the reader once the path is settled.
/// </summary>
internal enum JsonWalkResult
{
    /// <summary>The path reached a value the input itself closed.</summary>
    Resolved,

    /// <summary>
    /// The path reached a value only the repair closed: the reader ran out of
    /// text partway through the answer.
    /// </summary>
    Truncated,

    /// <summary>
    /// The path didn't resolve, and settling that left the reader short of
    /// whatever is wrong with the document — so nothing is raised.
    /// </summary>
    Abandoned,

    /// <summary>
    /// The path didn't resolve, and settling that took the reader all the way
    /// to where the document stopped making sense.
    /// </summary>
    Exhausted,
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
