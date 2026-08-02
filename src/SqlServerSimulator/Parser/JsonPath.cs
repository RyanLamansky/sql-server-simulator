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
/// Whitespace separates the grammar's tokens and may sit between any two of
/// them — around the mode keyword, either side of the <c>$</c>, either side
/// of a <c>.</c>, inside the brackets of an index, and trailing the path, so
/// <c>'  lax  $ . a [ 0 ] '</c> parses (probe-confirmed, as is the keyword
/// needing no whitespace behind it at all: <c>lax$.a</c>).
/// </para>
/// <para>
/// Quoted-property escape: a doubled <c>""</c> inside the quoted form is
/// one literal <c>"</c>, matching SQL Server. Other JSON Pointer-style
/// escapes aren't modeled — EF Core 10 doesn't depend on them.
/// </para>
/// </remarks>
internal readonly struct JsonPath
{
    /// <summary>
    /// The character Msg 13607 names for a path that ran out of text — a
    /// literal period standing in for the end, the same placeholder
    /// <see cref="JsonText"/>'s Msg 13609 scan uses.
    /// </summary>
    private const char EndOfPathCharacter = '.';

    /// <summary>
    /// Msg 13607's State where the parser wanted the <c>$</c>, or the
    /// <c>.</c> / <c>[</c> / end that follows a segment, or the name behind
    /// a <c>.</c>.
    /// </summary>
    private const byte StateAtSegmentStart = 22;

    /// <summary>
    /// Msg 13607's State for a path that ran out of text — and for the
    /// grammar's own punctuation wherever it turns up out of place, and for
    /// anything at all behind a quoted property name.
    /// </summary>
    private const byte StateAtEndOfPath = 14;

    /// <summary>Msg 13607's State inside <c>[</c>, where a digit was due.</summary>
    private const byte StateAtIndexDigits = 21;

    /// <summary>Msg 13607's State inside <c>[</c> past the digits, where the <c>]</c> was due.</summary>
    private const byte StateAtIndexClose = 15;

    /// <summary>Msg 13607's State for an index above real's <c>uint</c> ceiling.</summary>
    private const byte StateIndexOverflow = 16;

    /// <summary>Msg 13607's State for a quoted property name the path never closed.</summary>
    private const byte StateInQuotedName = 20;

    /// <summary>
    /// How many digits an index reads before real stops taking them, which
    /// decides where an over-ceiling index reports.
    /// </summary>
    private const int MaxIndexDigits = 11;

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
        if (acceptAppend && TryKeyword(text, ref i, "append"))
        {
            append = true;
            SkipWhitespace(text, ref i);
        }

        if (TryKeyword(text, ref i, "lax"))
        {
            SkipWhitespace(text, ref i);
        }
        else if (TryKeyword(text, ref i, "strict"))
        {
            mode = JsonPathMode.Strict;
            SkipWhitespace(text, ref i);
        }

        if (i >= text.Length || text[i] != '$')
            throw Malformed(text, i, StateAtSegmentStart);
        i++;
        SkipWhitespace(text, ref i);

        // The state a stray character reports depends on what the parser had
        // just read: everywhere but after a quoted property name it is
        // StateAtSegmentStart.
        var segmentState = StateAtSegmentStart;
        var segments = new List<Segment>();
        while (i < text.Length)
        {
            if (text[i] == '.')
            {
                i++;
                SkipWhitespace(text, ref i);
                if (i < text.Length && text[i] == '"')
                {
                    segments.Add(Segment.ForProperty(ReadQuotedName(text, ref i)));
                    segmentState = StateAtEndOfPath;
                }
                else
                {
                    // A name starts with a letter or an underscore; a digit
                    // there is one of the characters that reports state 14.
                    if (i >= text.Length || !(char.IsLetter(text[i]) || text[i] == '_'))
                        throw Malformed(text, i, StateAtSegmentStart);
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;
                    segments.Add(Segment.ForProperty(text[start..i]));
                    segmentState = StateAtSegmentStart;
                }
            }
            else if (text[i] == '[')
            {
                i++;
                segments.Add(Segment.ForIndex(ReadIndex(text, ref i)));
                segmentState = StateAtSegmentStart;
            }
            else
            {
                throw Malformed(text, i, segmentState);
            }

            SkipWhitespace(text, ref i);
        }

        return new JsonPath(mode, [.. segments], append);
    }

    /// <summary>
    /// Consumes <paramref name="keyword"/> when it stands as a whole word at
    /// <paramref name="i"/>. Real needs no whitespace behind one —
    /// <c>lax$.a</c> and <c>append$.a</c> both parse — but it does need the
    /// word to end there, so <c>laxx$.a</c> is malformed rather than a lax
    /// path (both probe-confirmed).
    /// </summary>
    private static bool TryKeyword(string text, ref int i, string keyword)
    {
        if (i + keyword.Length > text.Length || !text.AsSpan(i, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;
        var after = i + keyword.Length;
        if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
            return false;
        i = after;
        return true;
    }

    /// <summary>
    /// Reads a <c>"…"</c> property name from the opening quote, resolving the
    /// doubled <c>""</c> escape. Running off the end inside one is the single
    /// malformed-path case with a state of its own.
    /// </summary>
    private static string ReadQuotedName(string text, ref int i)
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
                return sb.ToString();
            }
            _ = sb.Append(text[i]);
            i++;
        }
        throw SimulatedSqlException.JsonInvalidPath(EndOfPathCharacter, text.Length, StateInQuotedName);
    }

    /// <summary>
    /// Reads an array index from just past its <c>[</c> through its <c>]</c>.
    /// Real's ceiling is <c>uint</c>'s, and it stops reading digits at the
    /// eleventh — so <c>$[4294967296]</c> reports its tenth digit while a
    /// twenty-digit run reports its eleventh (probe-confirmed). Anything
    /// above <see cref="int.MaxValue"/> clamps, since no array reaches that
    /// far and the index is only ever compared against one.
    /// </summary>
    private static int ReadIndex(string text, ref int i)
    {
        SkipWhitespace(text, ref i);
        var start = i;
        ulong value = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]) && i - start < MaxIndexDigits)
        {
            value = (value * 10) + (ulong)(text[i] - '0');
            i++;
        }
        if (i == start)
            throw Malformed(text, i, StateAtIndexDigits);
        if (value > uint.MaxValue)
            throw SimulatedSqlException.JsonInvalidPath(text[i - 1], i - 1, StateIndexOverflow);
        SkipWhitespace(text, ref i);
        if (i >= text.Length || text[i] != ']')
            throw Malformed(text, i, StateAtIndexClose);
        i++;
        return (int)Math.Min(value, int.MaxValue);
    }

    /// <summary>
    /// Msg 13607 for the character at <paramref name="i"/>, or for the end of
    /// the path when there is no character left.
    /// <paramref name="stateHere"/> is what the position reports for a
    /// character the grammar has no other opinion about.
    /// </summary>
    private static SimulatedSqlException Malformed(string text, int i, byte stateHere) =>
        i >= text.Length
            ? SimulatedSqlException.JsonInvalidPath(EndOfPathCharacter, text.Length, StateAtEndOfPath)
            : SimulatedSqlException.JsonInvalidPath(text[i], i, StateFor(text[i], stateHere));

    /// <summary>
    /// The grammar's own punctuation — and the digits an index is written
    /// with — report state 14 wherever they turn up out of place, whatever
    /// the position expected; every other character reports the position's
    /// own state (all probe-confirmed against SQL Server 2025).
    /// </summary>
    private static byte StateFor(char c, byte stateHere) =>
        c is '$' or '"' or '[' or ']' or '.' || char.IsAsciiDigit(c) ? StateAtEndOfPath : stateHere;

    private static void SkipWhitespace(string text, ref int i)
    {
        // Space, tab, line feed, form feed and carriage return — real takes
        // all five between the path's tokens and none of them inside a name.
        // Vertical tab and the non-breaking space are not whitespace here
        // (both probe-confirmed).
        while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\f' or '\r')
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
