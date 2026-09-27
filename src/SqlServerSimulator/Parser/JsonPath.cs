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
    /// Whether any segment is one of SQL Server 2025's advanced accessors — a
    /// wildcard (<c>[*]</c> or <c>.*</c>), a range, a list or <c>last</c> —
    /// which can select more than one value, or a value only the document's
    /// shape pins down.
    /// </summary>
    public readonly bool IsAdvanced;

    /// <summary>Whether any array accessor names <c>last</c>.</summary>
    public readonly bool HasLast;

    /// <summary>Whether any array accessor lists more than one item.</summary>
    public readonly bool HasComma;

    /// <summary>Whether any segment is <c>[*]</c> or <c>.*</c>.</summary>
    public readonly bool HasWildcard;

    /// <summary>
    /// Whether any range is written with two different endpoints, so it can
    /// select more than one element.
    /// </summary>
    public readonly bool HasWideRange;

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
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.PropertyWildcard or SegmentKind.ArrayWildcard:
                    this.IsAdvanced = this.HasWildcard = true;
                    break;
                case SegmentKind.ArraySelector:
                    this.IsAdvanced = true;
                    this.HasComma |= segment.Items!.Length > 1;
                    foreach (var item in segment.Items)
                    {
                        this.HasLast |= item.FromLast || item.ToLast;
                        this.HasWideRange |= item.From != item.To || item.FromLast != item.ToLast;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// This path's mode and prefix over other segments — how an advanced path
    /// whose every accessor settled on one element becomes a plain one.
    /// </summary>
    public JsonPath WithSegments(Segment[] segments) => new(this.Mode, segments, this.Append);

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
        var keywordStart = i;
        if (TryKeyword(text, ref i, "append"))
        {
            // Only JSON_MODIFY takes the prefix; every other reader knows the
            // word and refuses it at state 14 (probed 2026-09-27 against SQL
            // Server 2025).
            if (!acceptAppend)
                throw SimulatedSqlException.JsonInvalidPath(text[keywordStart], keywordStart, StateAtEndOfPath);
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
                else if (i < text.Length && text[i] == '*')
                {
                    // `.*` — every member of an object. Whatever follows it
                    // out of place reports state 14, as behind a quoted name.
                    i++;
                    segments.Add(Segment.PropertyWildcard);
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
                segments.Add(ReadArrayAccessor(text, ref i));
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
    /// Reads an array accessor from just past its <c>[</c> through its
    /// <c>]</c>: a plain index, the <c>*</c> wildcard, or SQL Server 2025's
    /// comma-separated list of items, each an index or <c>last</c>, or a
    /// range of two joined by <c>to</c>. The keywords are lower case only and
    /// <c>to</c> needs whitespace before it; a word where one of them was due
    /// reports state 21 when whitespace came first and 15 when it didn't, and
    /// a second <c>to</c> reports 14 (all probed 2026-09-27 against SQL
    /// Server 2025). A range written high-to-low in plain numbers is
    /// Msg 13660 state 1 as the path parses.
    /// </summary>
    private static Segment ReadArrayAccessor(string text, ref int i)
    {
        SkipWhitespace(text, ref i);
        if (i < text.Length && text[i] == '*')
        {
            i++;
            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != ']')
                throw Malformed(text, i, StateAtIndexDigits);
            i++;
            return Segment.ArrayWildcard;
        }

        var items = new List<ArrayItem>();
        while (true)
        {
            var from = ReadBound(text, ref i, out var fromLast, clampOverflow: false);
            var to = from;
            var toLast = fromLast;
            var isRange = false;
            var spaced = SkipSpacing(text, ref i);
            if (spaced && IsLowerKeywordAt(text, i, "to"))
            {
                i += 2;
                SkipWhitespace(text, ref i);
                to = ReadBound(text, ref i, out toLast, clampOverflow: true);
                isRange = true;
                spaced = SkipSpacing(text, ref i);
                if (spaced && IsLowerKeywordAt(text, i, "to"))
                    throw SimulatedSqlException.JsonInvalidPath(text[i], i, StateAtEndOfPath);
            }
            if (isRange && !fromLast && !toLast && from > to)
                throw SimulatedSqlException.JsonAdvancedAccessorNotSupported("Reversed indexing", 1);
            items.Add(new ArrayItem(from, fromLast, to, toLast, isRange));

            if (i < text.Length && text[i] == ',')
            {
                i++;
                SkipWhitespace(text, ref i);
                continue;
            }
            if (i < text.Length && text[i] == ']')
            {
                i++;
                break;
            }
            throw Malformed(text, i, spaced ? StateAtIndexDigits : StateAtIndexClose);
        }

        // `[0 to 0]` is a range for the accessor rules even though it names
        // one element, so only a lone index stays a plain one.
        return items is [{ IsRange: false, FromLast: false } only]
            ? Segment.ForIndex(only.From)
            : Segment.ForItems([.. items]);
    }

    /// <summary>
    /// Reads one end of an array item: digits, or the word <c>last</c>.
    /// Real's ceiling for an index is <c>uint</c>'s, and it stops reading
    /// digits at the eleventh — so <c>$[4294967296]</c> reports its tenth
    /// digit while a twenty-digit run reports its eleventh (probe-confirmed).
    /// A range's upper end takes a larger number without complaint
    /// (<c>[0 to 5000000000]</c> reads, probed 2026-09-27), which
    /// <paramref name="clampOverflow"/> asks for. Anything above
    /// <see cref="int.MaxValue"/> clamps, since no array reaches that far and
    /// the index is only ever compared against one.
    /// </summary>
    private static int ReadBound(string text, ref int i, out bool last, bool clampOverflow)
    {
        last = false;
        if (IsLowerKeywordAt(text, i, "last"))
        {
            i += 4;
            last = true;
            return 0;
        }
        var start = i;
        ulong value = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]) && i - start < MaxIndexDigits)
        {
            value = (value * 10) + (ulong)(text[i] - '0');
            i++;
        }
        if (i == start)
            throw Malformed(text, i, StateAtIndexDigits);
        if (value > uint.MaxValue && !clampOverflow)
            throw SimulatedSqlException.JsonInvalidPath(text[i - 1], i - 1, StateIndexOverflow);
        return (int)Math.Min(value, int.MaxValue);
    }

    /// <summary>
    /// Whether the lower-case <paramref name="keyword"/> stands at
    /// <paramref name="i"/> as a whole word. The array accessor's keywords
    /// match case-sensitively, unlike <c>lax</c> / <c>strict</c>.
    /// </summary>
    private static bool IsLowerKeywordAt(string text, int i, string keyword)
    {
        if (i + keyword.Length > text.Length || !text.AsSpan(i, keyword.Length).SequenceEqual(keyword))
            return false;
        var after = i + keyword.Length;
        return after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
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
        c is '$' or '"' or '[' or ']' or '.' or ',' or '*' || char.IsAsciiDigit(c) ? StateAtEndOfPath : stateHere;

    private static void SkipWhitespace(string text, ref int i)
    {
        // Space, tab, line feed, form feed and carriage return — real takes
        // all five between the path's tokens and none of them inside a name.
        // Vertical tab and the non-breaking space are not whitespace here
        // (both probe-confirmed).
        while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\f' or '\r')
            i++;
    }

    /// <summary><see cref="SkipWhitespace"/>, reporting whether there was any.</summary>
    private static bool SkipSpacing(string text, ref int i)
    {
        var start = i;
        SkipWhitespace(text, ref i);
        return i != start;
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

    /// <summary>
    /// Evaluates the path as SQL Server 2025's advanced accessors read it: a
    /// set of values rather than one, each segment mapping every value
    /// selected so far to what it names in it. Returns whether the path was
    /// found — every segment named something in at least one value, a
    /// wildcard over an empty container or a range running backwards past
    /// <c>last</c> counting as naming nothing successfully — and fills
    /// <paramref name="nodes"/> with what the last segment selected.
    /// <paramref name="partial"/> reports a segment that missed in some value
    /// it was applied to — an index past the end, an absent property, an
    /// accessor over the wrong kind of value — which <c>strict</c> reads as a
    /// miss even when the path was found elsewhere (probed 2026-09-27 against
    /// SQL Server 2025).
    /// </summary>
    public bool Select(JsonElement root, List<JsonElement> nodes, out bool partial)
    {
        partial = false;
        nodes.Clear();
        nodes.Add(root);
        var next = new List<JsonElement>();
        foreach (var segment in this.Segments)
        {
            next.Clear();
            var matched = false;
            foreach (var node in nodes)
            {
                matched |= SelectFrom(node, segment, next, out var complete);
                partial |= !complete;
            }
            nodes.Clear();
            if (!matched)
            {
                partial = true;
                return false;
            }
            nodes.AddRange(next);
        }
        return true;
    }

    /// <summary>
    /// Adds what <paramref name="segment"/> names in <paramref name="node"/>
    /// to <paramref name="into"/>, returning whether it named anything;
    /// <paramref name="complete"/> reports whether it named everything it
    /// asked for.
    /// </summary>
    private static bool SelectFrom(JsonElement node, in Segment segment, List<JsonElement> into, out bool complete)
    {
        complete = true;
        switch (segment.Kind)
        {
            case SegmentKind.Property:
                if (node.ValueKind == JsonValueKind.Object && FirstProperty(node, segment.Property!) is { } member)
                {
                    into.Add(member);
                    return true;
                }
                return complete = false;
            case SegmentKind.Index:
                if (node.ValueKind == JsonValueKind.Array && segment.Index < node.GetArrayLength())
                {
                    into.Add(node[segment.Index]);
                    return true;
                }
                return complete = false;
            case SegmentKind.PropertyWildcard:
                if (node.ValueKind != JsonValueKind.Object)
                    return complete = false;
                foreach (var property in node.EnumerateObject())
                    into.Add(property.Value);
                return true;
            case SegmentKind.ArrayWildcard:
                if (node.ValueKind != JsonValueKind.Array)
                    return complete = false;
                foreach (var element in node.EnumerateArray())
                    into.Add(element);
                return true;
            default:
                if (node.ValueKind != JsonValueKind.Array)
                    return complete = false;
                var length = node.GetArrayLength();
                var any = false;
                foreach (var item in segment.Items!)
                {
                    var from = item.FromLast ? length - 1 : item.From;
                    var to = item.ToLast ? length - 1 : item.To;
                    if (from > to && item.IsRange)
                    {
                        // Only `last` can run a range backwards here, which
                        // selects nothing without missing.
                        any = true;
                        continue;
                    }
                    if (from < 0 || to >= length)
                        complete = false;
                    for (var k = Math.Max(from, 0); k <= Math.Min(to, length - 1); k++)
                    {
                        into.Add(node[k]);
                        any = true;
                    }
                }
                return any;
        }
    }

    /// <summary>
    /// Resolves each array accessor of a path that names at most one element
    /// per step — a list of one item, <c>last</c>, a range with equal ends —
    /// to the plain index <c>JSON_MODIFY</c> writes through in
    /// <paramref name="root"/>. That is real's reading, not the selection
    /// <see cref="Select"/> makes: <c>JSON_MODIFY</c> over <c>json</c> writes
    /// <c>[last]</c> into the <em>first</em> element of a non-empty array
    /// (probed 2026-09-27 against SQL Server 2025). Returns null when an
    /// accessor lands on nothing (an empty array, a step into a missing
    /// value); a missing <em>property</em> is left to the caller's own walk,
    /// which knows what an absent member means to it.
    /// </summary>
    public JsonPath? ResolveForModify(JsonElement root)
    {
        var resolved = new Segment[this.Segments.Length];
        JsonElement? current = root;
        for (var s = 0; s < this.Segments.Length; s++)
        {
            var segment = this.Segments[s];
            if (segment.Kind == SegmentKind.ArraySelector)
            {
                if (current is not { ValueKind: JsonValueKind.Array } array)
                    return null;
                var item = segment.Items![0];
                var index = item.FromLast ? 0 : item.From;
                if (item.FromLast && array.GetArrayLength() == 0)
                    return null;
                segment = Segment.ForIndex(index);
            }
            resolved[s] = segment;
            current = current is { } node ? TryStep(node, segment) : null;
        }
        return this.WithSegments(resolved);
    }

    /// <summary>What a <see cref="Segment"/> reads.</summary>
    public enum SegmentKind : byte
    {
        /// <summary><c>.name</c> / <c>."name"</c>.</summary>
        Property,

        /// <summary>A lone <c>[n]</c>.</summary>
        Index,

        /// <summary><c>.*</c> — every member of an object.</summary>
        PropertyWildcard,

        /// <summary><c>[*]</c> — every element of an array.</summary>
        ArrayWildcard,

        /// <summary>
        /// Any other <c>[…]</c>: a list of indexes, <c>last</c> and ranges.
        /// </summary>
        ArraySelector,
    }

    /// <summary>
    /// One item of an array accessor's list: an index or <c>last</c>, or a
    /// range between two of them. A lone item has equal ends.
    /// </summary>
    public readonly struct ArrayItem(int from, bool fromLast, int to, bool toLast, bool isRange)
    {
        public readonly int From = from;
        public readonly bool FromLast = fromLast;
        public readonly int To = to;
        public readonly bool ToLast = toLast;

        /// <summary>Whether the item was written with <c>to</c>.</summary>
        public readonly bool IsRange = isRange;
    }

    /// <summary>One segment of a <see cref="JsonPath"/>; <see cref="Kind"/>
    /// says which fields it carries.</summary>
    public readonly struct Segment
    {
        public readonly SegmentKind Kind;
        public readonly bool IsIndex;
        public readonly int Index;
        public readonly string? Property;
        public readonly ArrayItem[]? Items;

        private Segment(SegmentKind kind, int index, string? property, ArrayItem[]? items)
        {
            this.Kind = kind;
            this.IsIndex = kind == SegmentKind.Index;
            this.Index = index;
            this.Property = property;
            this.Items = items;
        }

        public static Segment ForProperty(string name) => new(SegmentKind.Property, 0, name, null);
        public static Segment ForIndex(int index) => new(SegmentKind.Index, index, null, null);
        public static Segment ForItems(ArrayItem[] items) => new(SegmentKind.ArraySelector, 0, null, items);

        public static readonly Segment PropertyWildcard = new(SegmentKind.PropertyWildcard, 0, null, null);
        public static readonly Segment ArrayWildcard = new(SegmentKind.ArrayWildcard, 0, null, null);
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
